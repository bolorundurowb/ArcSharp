/*
 * ArcSharp runtime — Automatic Reference Counting support for the GC-less
 * executables produced by the ArcSharp compiler.
 *
 * Single-threaded; refcounts are non-atomic by design (see docs/architecture.md).
 *
 * Object memory layout (all slots are 8 bytes; "uniform slot" model):
 *   offset  0 : intptr_t strong        reference count
 *   offset  8 : intptr_t weak          weak count (+1 while object is live) in the
 *                                      low 32 bits, then the finalisation flags
 *   offset 16 : TypeInfo* type         type descriptor
 *   offset 24 : first field / array length / string length / WeakReference slot
 *   offset 32 : UTF-16 string units, or the element TypeInfo* of an array
 *   offset 40 : array elements (one or more 8-byte slots per element)
 */
/* getenv is the portable way to read runtime options. The MSVC headers mark it
   deprecated in favour of _dupenv_s, which is not portable. */
#define _CRT_SECURE_NO_WARNINGS
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct TypeInfo TypeInfo;

typedef struct ObjHeader {
    intptr_t  strong;
    intptr_t  weak;
    TypeInfo* type;
} ObjHeader;

/* The weak word holds the weak count in its low 32 bits. The two flags sit
   above it so the header keeps its three 8-byte slots, and every field offset
   in the emitter stays where it is. Only this file reads them. */
#define ARC_WEAK_COUNT_MASK   ((intptr_t)0xFFFFFFFF)
#define ARC_FLAG_NO_FINALISE  ((intptr_t)1 << 32) /* GC.SuppressFinalize was called  */
#define ARC_FLAG_FINALISING   ((intptr_t)1 << 33) /* the finaliser chain is running  */
#define WEAK_COUNT(h)         ((h)->weak & ARC_WEAK_COUNT_MASK)

/* Strong-edge metadata emitted next to each class and struct TypeInfo.
   kind 1 is a strong reference slot. kind 2 is an inline struct, and nested
   points at that struct's TypeInfo so the walk continues at this offset.
   Weak slots are omitted: they are not ownership edges. The registry that
   uses this metadata never retains, releases, or frees the objects it sees. */
typedef struct ArcSlot {
    intptr_t  offset;
    int32_t   kind;
    int32_t   flags;
    TypeInfo* nested;
} ArcSlot;

struct TypeInfo {
    const char* name;
    intptr_t    instanceSize;
    void      (*deinit)(void*);
    void**      vtable;
    intptr_t    vtableLen;
    void**      itable;
    intptr_t    itableLen;
    TypeInfo*   base;
    ArcSlot*    slots;
    intptr_t    slotCount;
    /* Runs the user finalisers of this class and its bases, derived first.
       Null when the class and its bases declare none. It is the last field so
       the runtime's own descriptors, which list seven, leave it null. */
    void      (*finalizer)(void*);
};

int arc_is_instance(void* obj, TypeInfo* target);

/* ---- accounting (for verification / leak detection) ------------------ */
/* long long, not long: C long is 32 bits on Windows (LLP64). */
static long long g_alloc = 0;   /* total objects allocated                    */
static long long g_dead  = 0;   /* objects whose strong count reached 0        */
static long long g_free  = 0;   /* allocations actually freed (weak hit 0)     */
static long long g_retain = 0;  /* strong-count increments after allocation   */
static long long g_release = 0; /* strong-count decrements                     */

/* Registry of objects with storage still owned by ARC. Nodes are plain
   malloc blocks and do not count towards ARC accounting. */
typedef struct ArcRec {
    ObjHeader*    obj;
    const char*   site;
    struct ArcRec* next;
} ArcRec;

static ArcRec* g_recs = 0;
static ArcRec* g_quarantine = 0;
static int g_leaks = -1; /* -1 unknown, 0 off, 1 on */
static int g_checks = -1;
static int g_stats = -1;
static void*** g_roots = 0;
static intptr_t g_root_count = 0;
static void arc_report_leaks(void);

static void check_fail(const char* operation, const char* reason) {
    fprintf(stderr, "[arc-check] %s: %s\n", operation, reason);
    exit(70);
}

static void free_quarantine(void) {
    while (g_quarantine) {
        ArcRec* r = g_quarantine;
        g_quarantine = r->next;
        free(r->obj);
        free(r);
    }
}

static int checks_on(void) {
    if (g_checks < 0) {
        const char* e = getenv("ARC_CHECK");
        g_checks = e && strcmp(e, "1") == 0;
        if (g_checks && atexit(free_quarantine) != 0) {
            check_fail("initialisation", "cannot register quarantine cleanup");
        }
    }
    return g_checks;
}

static void check_header(const char* operation, const ObjHeader* h, int needs_live) {
    if (!checks_on()) {
        return;
    }
    for (ArcRec* r = g_recs; r; r = r->next) {
        if (r->obj == h) {
            if (h->strong < 0) {
                check_fail(operation, "negative strong count");
            }
            if (needs_live && h->strong == 0) {
                check_fail(operation, "dead object");
            }
            if (WEAK_COUNT(h) == 0) {
                check_fail(operation, "non-positive weak count");
            }
            return;
        }
    }
    check_fail(operation, "freed or untracked object");
}

static int leaks_on(void) {
    if (g_leaks < 0) {
        const char* e = getenv("ARC_LEAKS");
        g_leaks = (e && e[0] != '\0' && e[0] != '0') ? 1 : 0;
    }
    return g_leaks;
}

/* Printed only. The counters themselves always advance, so a report from an
   early runtime failure still shows the operations performed so far. */
static int stats_on(void) {
    if (g_stats < 0) {
        const char* e = getenv("ARC_STATS");
        g_stats = e && strcmp(e, "1") == 0;
    }
    return g_stats;
}

static void track(ObjHeader* h, const char* site) {
    ArcRec* r;
    if (!checks_on() && !leaks_on()) {
        return;
    }
    r = (ArcRec*)malloc(sizeof(ArcRec));
    if (!r) {
        if (checks_on()) {
            check_fail("allocation", "cannot track object");
        }
        return;
    }
    r->obj = h;
    r->site = site ? site : "-";
    r->next = g_recs;
    g_recs = r;
}

static void retire(ObjHeader* h) {
    ArcRec** pp;
    pp = &g_recs;
    while (*pp) {
        if ((*pp)->obj == h) {
            ArcRec* dead = *pp;
            *pp = dead->next;
            if (checks_on()) {
                /* Prevent address reuse from making a stale pointer appear valid. */
                dead->next = g_quarantine;
                g_quarantine = dead;
                return;
            }
            free(dead);
            break;
        }
        pp = &(*pp)->next;
    }
    free(h);
}

void arc_set_roots(void*** roots, intptr_t count) {
    g_roots = roots;
    g_root_count = count;
}

void arc_report(void) {
    fprintf(stderr, "[arc] alloc=%lld dead=%lld freed=%lld live=%lld\n",
            g_alloc, g_dead, g_free, g_alloc - g_dead);
    if (stats_on()) {
        fprintf(stderr, "[arc-stats] retain=%lld release=%lld\n",
                g_retain, g_release);
    }
    if (leaks_on()) arc_report_leaks();
}
long long arc_live_count(void) { return g_alloc - g_dead; }

/* ---- core retain / release ------------------------------------------- */
void* arc_alloc(TypeInfo* type, const char* site) {
    ObjHeader* h = (ObjHeader*)calloc(1, (size_t)type->instanceSize);
    h->strong = 1;
    h->weak   = 1;
    h->type   = type;
    g_alloc++;
    track(h, site);
    return h;
}

void arc_retain(void* p) {
    if (p) {
        ObjHeader* h = (ObjHeader*)p;
        check_header("retain", h, 1);
        h->strong++;
        g_retain++;
    }
}

void arc_weak_retain(void* p) {
    if (p) {
        ObjHeader* h = (ObjHeader*)p;
        check_header("weak retain", h, 0);
        h->weak++;
    }
}

void arc_weak_release(void* p) {
    if (!p) return;
    ObjHeader* h = (ObjHeader*)p;
    check_header("weak release", h, 0);
    h->weak--;
    if (WEAK_COUNT(h) == 0) {
        if (checks_on() && h->strong > 0) {
            check_fail("weak release", "lost implicit weak reference");
        }
        retire(h);
        g_free++;
    }
}

/* ---- exceptions ------------------------------------------------------- */
/* An exception in flight is the one reference held in arc_exception, and it
   counts as an owned (+1) reference. Compiled code never unwinds the stack.
   After a call or a runtime check that can fail, it loads arc_exception and
   leaves through its cleanup code when the value is not null. That is how
   every unwind path runs the same releases, and the same finalisers, as a
   normal exit. A catch block takes the reference out of the global, and the
   code that handles a failure inside a finaliser or at the top of Main
   releases it.

   The language raises exceptions without running a constructor. The class of
   System.Exception keeps its message in the first field, at offset 24, and
   the classes the runtime raises are registered by the compiled program in the
   order of the ARC_EXC_* values below. */
void* arc_exception = 0;

enum {
    ARC_EXC_NULL_REFERENCE,
    ARC_EXC_INDEX_OUT_OF_RANGE,
    ARC_EXC_INVALID_CAST,
    ARC_EXC_DIVIDE_BY_ZERO,
    ARC_EXC_OVERFLOW,
    ARC_EXC_ARRAY_TYPE_MISMATCH,
    ARC_EXC_ARGUMENT_NULL,
    ARC_EXC_ARGUMENT_OUT_OF_RANGE,
    ARC_EXC_FORMAT,
    ARC_EXC_ARGUMENT,
    ARC_EXC_KIND_COUNT
};

#define ARC_EXC_MESSAGE_OFFSET 24

static TypeInfo** g_exc_types = 0;
static intptr_t g_exc_count = 0;

void arc_release(void* p);
static void* arc_str_new(const char* data, intptr_t len, const char* site);
static void arc_str_write(FILE* stream, void* s);

void arc_set_exception_types(TypeInfo** types, intptr_t count) {
    g_exc_types = types;
    g_exc_count = count;
}

void arc_throw(void* ex);

static void arc_raise(int kind, const char* message) {
    TypeInfo* type = (g_exc_types && kind < g_exc_count) ? g_exc_types[kind] : 0;
    void* ex;
    if (!type) {
        fprintf(stderr, "RuntimeError: %s\n", message);
        arc_report();
        exit(70);
    }
    ex = arc_alloc(type, "<runtime>");
    *(void**)((char*)ex + ARC_EXC_MESSAGE_OFFSET) = arc_str_new(message, (intptr_t)strlen(message), "<runtime>");
    arc_throw(ex);
}

/* Takes the caller's reference. Throwing null raises NullReferenceException. */
void arc_throw(void* ex) {
    if (!ex) {
        arc_raise(ARC_EXC_NULL_REFERENCE, "Object reference not set to an instance of an object.");
        return;
    }
    if (arc_exception) {
        void* old = arc_exception;
        arc_exception = 0;
        arc_release(old);
    }
    arc_exception = ex;
}

/* Prints "Type: message", the form used for an unhandled exception and for one
   that escaped a finaliser. */
static void arc_print_exception(void* ex) {
    ObjHeader* h = (ObjHeader*)ex;
    void* message = *(void**)((char*)ex + ARC_EXC_MESSAGE_OFFSET);
    const char* name = (h->type && h->type->name) ? h->type->name : "?";
    if (message) {
        fprintf(stderr, "%s: ", name);
        arc_str_write(stderr, message);
        fputc('\n', stderr);
    } else {
        fprintf(stderr, "%s\n", name);
    }
}

/* Called when Main returns with an exception in flight. The reference is
   released first so the accounting line still shows a balanced program. */
void arc_unhandled(void) {
    void* ex = arc_exception;
    arc_exception = 0;
    fputs("Unhandled exception. ", stderr);
    arc_print_exception(ex);
    arc_release(ex);
    arc_report();
    exit(70);
}

/* A finaliser that throws is reported and then ignored: the exception does not
   reach the code that released the last reference, and teardown carries on. */
void arc_finaliser_exception(void* self) {
    void* ex = arc_exception;
    ObjHeader* h = (ObjHeader*)self;
    if (!ex) return;
    arc_exception = 0;
    fprintf(stderr, "[arc] exception in finaliser of %s ignored: ",
            (h && h->type && h->type->name) ? h->type->name : "?");
    arc_print_exception(ex);
    arc_release(ex);
}

void arc_null_check(void* p) {
    if (!p) {
        arc_raise(ARC_EXC_NULL_REFERENCE, "Object reference not set to an instance of an object.");
    }
}

void arc_cast_check(void* obj, TypeInfo* target) {
    char message[256];
    if (!obj || arc_is_instance(obj, target)) return;
    snprintf(message, sizeof message, "Unable to cast object of type '%s' to type '%s'.",
             ((ObjHeader*)obj)->type->name, target->name);
    arc_raise(ARC_EXC_INVALID_CAST, message);
}

void arc_overflow_check(int overflowed) {
    if (overflowed) {
        arc_raise(ARC_EXC_OVERFLOW, "Arithmetic operation resulted in an overflow.");
    }
}

/* Integer division and remainder. bits is 0 for an unsigned operation, which
   can only fail on a zero divisor, and 32 or 64 for a signed one, which can
   also overflow. The operands arrive widened to 64 bits. */
void arc_div_check(long long a, long long b, int bits) {
    if (b == 0) {
        arc_raise(ARC_EXC_DIVIDE_BY_ZERO, "Attempted to divide by zero.");
    } else if (b == -1 && ((bits == 32 && a == INT32_MIN) || (bits == 64 && a == INT64_MIN))) {
        arc_raise(ARC_EXC_OVERFLOW, "Arithmetic operation resulted in an overflow.");
    }
}

/* ---- decimal ---------------------------------------------------------- */
/* A decimal is two 8-byte slots, the layout of a struct with two long fields.
   Slot 0 holds the low 64 bits of the 96-bit mantissa. Slot 1 holds the high
   32 bits of the mantissa in bits 0-31, the scale (0 to 28, a power of ten to
   divide by) in bits 32-39, and the sign in bit 63. The arithmetic follows
   System.Decimal: mantissas are widened to 256 bits, and a result is rounded
   half to even until it fits 96 bits and a scale of 28. */
#define DEC_MAX_SCALE 28
#define BIG_LIMBS 8

typedef struct { uint32_t w[BIG_LIMBS]; } DecBig;
typedef struct { uint32_t m[3]; int scale; int neg; } DecValue;

static void big_set_u64(DecBig* a, uint64_t v) {
    memset(a, 0, sizeof *a);
    a->w[0] = (uint32_t)v;
    a->w[1] = (uint32_t)(v >> 32);
}

static void big_from_mantissa(DecBig* a, const DecValue* d) {
    memset(a, 0, sizeof *a);
    a->w[0] = d->m[0];
    a->w[1] = d->m[1];
    a->w[2] = d->m[2];
}

static int big_is_zero(const DecBig* a) {
    int i;
    for (i = 0; i < BIG_LIMBS; i++) {
        if (a->w[i]) return 0;
    }
    return 1;
}

static int big_cmp(const DecBig* a, const DecBig* b) {
    int i;
    for (i = BIG_LIMBS - 1; i >= 0; i--) {
        if (a->w[i] != b->w[i]) return a->w[i] > b->w[i] ? 1 : -1;
    }
    return 0;
}

static void big_mul_u32(DecBig* a, uint32_t m) {
    uint64_t carry = 0;
    int i;
    for (i = 0; i < BIG_LIMBS; i++) {
        uint64_t t = (uint64_t)a->w[i] * m + carry;
        a->w[i] = (uint32_t)t;
        carry = t >> 32;
    }
}

static uint32_t big_div_u32(DecBig* a, uint32_t d) {
    uint64_t rem = 0;
    int i;
    for (i = BIG_LIMBS - 1; i >= 0; i--) {
        uint64_t cur = (rem << 32) | a->w[i];
        a->w[i] = (uint32_t)(cur / d);
        rem = cur % d;
    }
    return (uint32_t)rem;
}

static void big_add(DecBig* a, const DecBig* b) {
    uint64_t carry = 0;
    int i;
    for (i = 0; i < BIG_LIMBS; i++) {
        uint64_t t = (uint64_t)a->w[i] + b->w[i] + carry;
        a->w[i] = (uint32_t)t;
        carry = t >> 32;
    }
}

/* a must not be smaller than b. */
static void big_sub(DecBig* a, const DecBig* b) {
    int64_t borrow = 0;
    int i;
    for (i = 0; i < BIG_LIMBS; i++) {
        int64_t t = (int64_t)a->w[i] - b->w[i] - borrow;
        borrow = t < 0;
        a->w[i] = (uint32_t)t;
    }
}

static void big_shl1(DecBig* a) {
    uint32_t carry = 0;
    int i;
    for (i = 0; i < BIG_LIMBS; i++) {
        uint32_t next = a->w[i] >> 31;
        a->w[i] = (a->w[i] << 1) | carry;
        carry = next;
    }
}

static int big_bit_length(const DecBig* a) {
    int i;
    for (i = BIG_LIMBS - 1; i >= 0; i--) {
        if (a->w[i]) {
            int bits = 32;
            uint32_t top = a->w[i];
            while (!(top & 0x80000000u)) {
                top <<= 1;
                bits--;
            }
            return i * 32 + bits;
        }
    }
    return 0;
}

/* Binary long division. The operands are at most 256 bits, so this is slow
   but small and exact. */
static void big_divmod(const DecBig* n, const DecBig* d, DecBig* q, DecBig* r) {
    int bit;
    memset(q, 0, sizeof *q);
    memset(r, 0, sizeof *r);
    for (bit = big_bit_length(n) - 1; bit >= 0; bit--) {
        big_shl1(r);
        r->w[0] |= (n->w[bit / 32] >> (bit % 32)) & 1u;
        if (big_cmp(r, d) >= 0) {
            big_sub(r, d);
            q->w[bit / 32] |= 1u << (bit % 32);
        }
    }
}

static void big_pow10(DecBig* out, int n) {
    big_set_u64(out, 1);
    while (n-- > 0) big_mul_u32(out, 10);
}

static int big_fits_96(const DecBig* a) {
    int i;
    for (i = 3; i < BIG_LIMBS; i++) {
        if (a->w[i]) return 0;
    }
    return 1;
}

/* a * b where the product is known to fit 256 bits. */
static void big_mul(const DecBig* a, const DecBig* b, DecBig* out) {
    int i, j;
    memset(out, 0, sizeof *out);
    for (i = 0; i < BIG_LIMBS; i++) {
        uint64_t carry = 0;
        for (j = 0; i + j < BIG_LIMBS; j++) {
            uint64_t t = (uint64_t)a->w[i] * b->w[j] + out->w[i + j] + carry;
            out->w[i + j] = (uint32_t)t;
            carry = t >> 32;
        }
    }
}

static void dec_load(const void* p, DecValue* d) {
    const uint64_t* slots = (const uint64_t*)p;
    d->m[0] = (uint32_t)slots[0];
    d->m[1] = (uint32_t)(slots[0] >> 32);
    d->m[2] = (uint32_t)slots[1];
    d->scale = (int)((slots[1] >> 32) & 0xFF);
    d->neg = (int)(slots[1] >> 63);
}

static void dec_store(void* p, const DecValue* d) {
    uint64_t* slots = (uint64_t*)p;
    slots[0] = (uint64_t)d->m[0] | ((uint64_t)d->m[1] << 32);
    slots[1] = (uint64_t)d->m[2] | ((uint64_t)d->scale << 32) | ((uint64_t)(d->neg ? 1 : 0) << 63);
}

static int dec_is_zero(const DecValue* d) {
    return !d->m[0] && !d->m[1] && !d->m[2];
}

static void dec_overflow(void* out) {
    DecValue zero;
    memset(&zero, 0, sizeof zero);
    if (out) dec_store(out, &zero);
    arc_raise(ARC_EXC_OVERFLOW, "Value was either too large or too small for a Decimal.");
}

/* Rounds value / 10^scale down to a 96-bit mantissa and a scale of at most 28,
   half to even, and stores it. Returns 0, or 1 when the value does not fit. */
static int dec_pack(const DecBig* value, int scale, int neg, void* out) {
    DecBig cur;
    DecValue result;
    int n = scale > DEC_MAX_SCALE ? scale - DEC_MAX_SCALE : 0;
    for (;;) {
        if (n > scale) return 1;
        if (n == 0) {
            cur = *value;
        } else {
            DecBig p, q, r, twice, one;
            int c;
            big_pow10(&p, n);
            big_divmod(value, &p, &q, &r);
            twice = r;
            big_shl1(&twice);
            c = big_cmp(&twice, &p);
            if (c > 0 || (c == 0 && (q.w[0] & 1u))) {
                big_set_u64(&one, 1);
                big_add(&q, &one);
            }
            cur = q;
        }
        if (big_fits_96(&cur)) break;
        n++;
    }
    result.m[0] = cur.w[0];
    result.m[1] = cur.w[1];
    result.m[2] = cur.w[2];
    result.scale = scale - n;
    result.neg = big_is_zero(&cur) ? 0 : neg;
    dec_store(out, &result);
    return 0;
}

/* Multiplies a mantissa by 10^k, widening it to the common scale. */
static void dec_align(const DecValue* d, int k, DecBig* out) {
    DecBig p;
    DecBig base;
    big_from_mantissa(&base, d);
    big_pow10(&p, k);
    big_mul(&base, &p, out);
}

static void dec_add_signed(void* a, void* b, void* out, int negate_b) {
    DecValue x, y;
    DecBig left, right;
    int scale, left_neg, right_neg;
    dec_load(a, &x);
    dec_load(b, &y);
    scale = x.scale > y.scale ? x.scale : y.scale;
    dec_align(&x, scale - x.scale, &left);
    dec_align(&y, scale - y.scale, &right);
    left_neg = x.neg;
    right_neg = negate_b ? !y.neg : y.neg;
    if (left_neg == right_neg) {
        big_add(&left, &right);
        if (dec_pack(&left, scale, left_neg, out)) dec_overflow(out);
    } else if (big_cmp(&left, &right) >= 0) {
        big_sub(&left, &right);
        if (dec_pack(&left, scale, left_neg, out)) dec_overflow(out);
    } else {
        big_sub(&right, &left);
        if (dec_pack(&right, scale, right_neg, out)) dec_overflow(out);
    }
}

void arc_dec_set(void* out, int64_t low, int64_t high) {
    int64_t* slots = (int64_t*)out;
    slots[0] = low;
    slots[1] = high;
}

void arc_dec_add(void* a, void* b, void* out) { dec_add_signed(a, b, out, 0); }
void arc_dec_sub(void* a, void* b, void* out) { dec_add_signed(a, b, out, 1); }

void arc_dec_neg(void* a, void* out) {
    DecValue x;
    dec_load(a, &x);
    x.neg = dec_is_zero(&x) ? 0 : !x.neg;
    dec_store(out, &x);
}

void arc_dec_mul(void* a, void* b, void* out) {
    DecValue x, y;
    DecBig left, right, product;
    dec_load(a, &x);
    dec_load(b, &y);
    big_from_mantissa(&left, &x);
    big_from_mantissa(&right, &y);
    big_mul(&left, &right, &product);
    if (dec_pack(&product, x.scale + y.scale, x.neg != y.neg, out)) dec_overflow(out);
}

/* The quotient keeps as many decimal places as fit 96 bits and a scale of 28.
   An exact quotient then drops trailing zeros, but never below the scale the
   operands imply, so 6.0 / 2 is 3.0 and 1 / 4 is 0.25. */
void arc_dec_div(void* a, void* b, void* out) {
    DecValue x, y;
    DecBig dividend, divisor, limit;
    int preferred, scale;
    dec_load(a, &x);
    dec_load(b, &y);
    if (dec_is_zero(&y)) {
        DecValue zero;
        memset(&zero, 0, sizeof zero);
        dec_store(out, &zero);
        arc_raise(ARC_EXC_DIVIDE_BY_ZERO, "Attempted to divide by zero.");
        return;
    }
    big_from_mantissa(&divisor, &y);
    memset(&limit, 0, sizeof limit);
    limit.w[3] = 1;
    preferred = x.scale > y.scale ? x.scale - y.scale : 0;
    for (scale = DEC_MAX_SCALE; scale >= preferred; scale--) {
        DecBig scaled, q, r, twice, one;
        int exact, c;
        dec_align(&x, scale + y.scale - x.scale, &scaled);
        big_divmod(&scaled, &divisor, &q, &r);
        exact = big_is_zero(&r);
        twice = r;
        big_shl1(&twice);
        c = big_cmp(&twice, &divisor);
        if (c > 0 || (c == 0 && (q.w[0] & 1u))) {
            big_set_u64(&one, 1);
            big_add(&q, &one);
        }
        if (big_cmp(&q, &limit) >= 0) continue;
        if (exact) {
            for (;;) {
                DecBig shorter = q;
                if (scale <= preferred || big_div_u32(&shorter, 10) != 0) break;
                q = shorter;
                scale--;
            }
        }
        {
            DecValue result;
            result.m[0] = q.w[0];
            result.m[1] = q.w[1];
            result.m[2] = q.w[2];
            result.scale = scale;
            result.neg = (x.neg != y.neg) && !big_is_zero(&q);
            dec_store(out, &result);
        }
        return;
    }
    dec_overflow(out);
}

void arc_dec_rem(void* a, void* b, void* out) {
    DecValue x, y;
    DecBig left, right, q, r;
    int scale;
    dec_load(a, &x);
    dec_load(b, &y);
    if (dec_is_zero(&y)) {
        DecValue zero;
        memset(&zero, 0, sizeof zero);
        dec_store(out, &zero);
        arc_raise(ARC_EXC_DIVIDE_BY_ZERO, "Attempted to divide by zero.");
        return;
    }
    scale = x.scale > y.scale ? x.scale : y.scale;
    dec_align(&x, scale - x.scale, &left);
    dec_align(&y, scale - y.scale, &right);
    big_divmod(&left, &right, &q, &r);
    if (dec_pack(&r, scale, x.neg, out)) dec_overflow(out);
}

int arc_dec_cmp(void* a, void* b) {
    DecValue x, y;
    DecBig left, right;
    int scale, c;
    dec_load(a, &x);
    dec_load(b, &y);
    if (dec_is_zero(&x)) x.neg = 0;
    if (dec_is_zero(&y)) y.neg = 0;
    if (x.neg != y.neg) return x.neg ? -1 : 1;
    scale = x.scale > y.scale ? x.scale : y.scale;
    dec_align(&x, scale - x.scale, &left);
    dec_align(&y, scale - y.scale, &right);
    c = big_cmp(&left, &right);
    return x.neg ? -c : c;
}

void arc_dec_from_i64(int64_t v, void* out) {
    DecBig magnitude;
    big_set_u64(&magnitude, v < 0 ? (uint64_t)0 - (uint64_t)v : (uint64_t)v);
    dec_pack(&magnitude, 0, v < 0, out);
}

void arc_dec_from_u64(int64_t v, void* out) {
    DecBig magnitude;
    big_set_u64(&magnitude, (uint64_t)v);
    dec_pack(&magnitude, 0, 0, out);
}

/* Right shift by k bits, rounding half to even. */
static void big_shr_round(const DecBig* a, int k, DecBig* out) {
    int i;
    int half = 0;
    int sticky = 0;
    memset(out, 0, sizeof *out);
    for (i = 0; i < k && i < BIG_LIMBS * 32; i++) {
        int bit = (int)((a->w[i / 32] >> (i % 32)) & 1u);
        if (i == k - 1) half = bit;
        else if (bit) sticky = 1;
    }
    if (k < BIG_LIMBS * 32) {
        for (i = k; i < BIG_LIMBS * 32; i++) {
            if ((a->w[i / 32] >> (i % 32)) & 1u) out->w[(i - k) / 32] |= 1u << ((i - k) % 32);
        }
    }
    if (half && (sticky || (out->w[0] & 1u))) {
        DecBig one;
        big_set_u64(&one, 1);
        big_add(out, &one);
    }
}

/* Like System.Decimal in current .NET, the exact binary value is kept, with
   the largest scale (at most 28) that still fits 96 bits, and trailing zeros
   are dropped. The caller guarantees a finite value below 2^96. */
void arc_dec_from_double(double v, void* out) {
    DecBig magnitude;
    DecBig factor;
    DecBig scaled;
    DecBig quotient;
    uint64_t mantissa, bits;
    int exponent, scale;
    int neg = v < 0;
    if (v != v || v >= 7.9228162514264337593543950336e28 || v <= -7.9228162514264337593543950336e28) {
        dec_overflow(out);
        return;
    }
    if (neg) v = -v;
    memcpy(&bits, &v, sizeof bits);
    exponent = (int)((bits >> 52) & 0x7ffu);
    mantissa = bits & 0xfffffffffffffull;
    if (exponent == 0) {
        exponent = -1074;
    } else {
        mantissa |= 1ull << 52;
        exponent -= 1075;
    }
    if (exponent >= 0) {
        big_set_u64(&magnitude, mantissa);
        while (exponent-- > 0) big_shl1(&magnitude);
        if (dec_pack(&magnitude, 0, neg, out)) dec_overflow(out);
        return;
    }
    for (scale = DEC_MAX_SCALE; scale >= 0; scale--) {
        big_set_u64(&magnitude, mantissa);
        big_pow10(&factor, scale);
        big_mul(&magnitude, &factor, &scaled);
        big_shr_round(&scaled, -exponent, &quotient);
        if (big_fits_96(&quotient)) break;
    }
    while (scale > 0) {
        DecBig shorter = quotient;
        if (big_div_u32(&shorter, 10) != 0) break;
        quotient = shorter;
        scale--;
    }
    if (dec_pack(&quotient, scale, neg, out)) dec_overflow(out);
}
static void dec_truncate(const DecValue* d, DecBig* out) {
    DecBig p;
    big_from_mantissa(out, d);
    if (d->scale > 0) {
        DecBig r;
        DecBig q;
        big_pow10(&p, d->scale);
        big_divmod(out, &p, &q, &r);
        *out = q;
    }
}

static const char* dec_integer_name(int64_t min, int64_t max) {
    if (min == -128) return "an SByte";
    if (min == 0 && max == 255) return "a Byte";
    if (min == -32768) return "an Int16";
    if (min == 0 && max == 65535) return "a UInt16";
    if (min == INT32_MIN) return "an Int32";
    if (min == 0 && max == (int64_t)UINT32_MAX) return "a UInt32";
    return "an Int64";
}

/* Truncates toward zero. A value outside [min, max] raises OverflowException
   whatever the checked context, as in .NET. */
int64_t arc_dec_to_i64(void* a, int64_t min, int64_t max) {
    DecValue x;
    DecBig whole;
    uint64_t magnitude;
    char message[96];
    dec_load(a, &x);
    dec_truncate(&x, &whole);
    magnitude = ((uint64_t)whole.w[1] << 32) | whole.w[0];
    if (big_fits_96(&whole) && whole.w[2] == 0) {
        if (!x.neg && magnitude <= (uint64_t)max) return (int64_t)magnitude;
        if (x.neg && (magnitude == 0 || magnitude <= (uint64_t)0 - (uint64_t)min)) {
            return (int64_t)((uint64_t)0 - magnitude);
        }
    }
    snprintf(message, sizeof message, "Value was either too large or too small for %s.", dec_integer_name(min, max));
    arc_raise(ARC_EXC_OVERFLOW, message);
    return 0;
}

int64_t arc_dec_to_u64(void* a) {
    DecValue x;
    DecBig whole;
    dec_load(a, &x);
    dec_truncate(&x, &whole);
    if (big_fits_96(&whole) && whole.w[2] == 0 && (!x.neg || big_is_zero(&whole))) {
        return (int64_t)(((uint64_t)whole.w[1] << 32) | whole.w[0]);
    }
    arc_raise(ARC_EXC_OVERFLOW, "Value was either too large or too small for a UInt64.");
    return 0;
}

double arc_dec_to_double(void* a) {
    static const double powers[DEC_MAX_SCALE + 1] = {
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
        1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22, 1e23, 1e24, 1e25, 1e26, 1e27, 1e28
    };
    DecValue x;
    double result;
    dec_load(a, &x);
    result = ((double)(((uint64_t)x.m[1] << 32) | x.m[0]) + (double)x.m[2] * 18446744073709551616.0)
        / powers[x.scale > DEC_MAX_SCALE ? DEC_MAX_SCALE : x.scale];
    return x.neg ? -result : result;
}

void* arc_dec_to_str(void* a) {
    DecValue x;
    DecBig magnitude;
    char digits[40];
    char text[48];
    int count = 0, length = 0, i;
    dec_load(a, &x);
    big_from_mantissa(&magnitude, &x);
    do {
        digits[count++] = (char)('0' + big_div_u32(&magnitude, 10));
    } while (!big_is_zero(&magnitude));
    while (count <= x.scale) digits[count++] = '0';
    if (x.neg && !dec_is_zero(&x)) text[length++] = '-';
    for (i = count - 1; i >= 0; i--) {
        text[length++] = digits[i];
        if (i == x.scale && i > 0) text[length++] = '.';
    }
    return arc_str_new(text, length, "<runtime>");
}

/* ---- destruction ------------------------------------------------------ */
/* An object whose strong count reached zero goes through three steps: its user
   finalisers, the generated teardown that releases its fields, and the drop of
   its implicit weak count. A child that reaches zero during the teardown is
   queued instead of destroyed on the spot, so a long chain of objects is
   destroyed by a loop and not by one stack frame per link. The queue is
   ordered so the children are destroyed in the order a recursive release
   would have used: the first field's whole subtree, then the second field's. */
static ObjHeader** g_pending = 0;
static size_t g_pending_len = 0;
static size_t g_pending_cap = 0;
static int g_in_teardown = 0;

static void pending_push(ObjHeader* h) {
    if (g_pending_len == g_pending_cap) {
        size_t cap = g_pending_cap ? g_pending_cap * 2 : 64;
        ObjHeader** grown = (ObjHeader**)realloc(g_pending, cap * sizeof(ObjHeader*));
        if (!grown) {
            fprintf(stderr, "OutOfMemoryException: cannot queue an object for destruction\n");
            exit(70);
        }
        g_pending = grown;
        g_pending_cap = cap;
    }
    g_pending[g_pending_len++] = h;
}

void arc_report(void);

/* The count is held at one while the finalisers run. Their own retains and
   releases of the object, such as a call on 'this', then balance without
   reaching zero a second time. A count above one afterwards means a finaliser
   stored the object somewhere, which would hand out a reference to memory that
   is about to be freed. */
static void run_finalisers(ObjHeader* h) {
    TypeInfo* t = h->type;
    void* outer;
    if (!t || !t->finalizer || (h->weak & ARC_FLAG_NO_FINALISE)) return;
    /* A release can happen while an exception is in flight, on an unwind path.
       The finaliser must not see that exception as its own, so it is set aside
       and put back afterwards. */
    outer = arc_exception;
    arc_exception = 0;
    h->weak |= ARC_FLAG_FINALISING;
    h->strong = 1;
    t->finalizer(h);
    arc_finaliser_exception(h);
    arc_exception = outer;
    if (--h->strong != 0) {
        fprintf(stderr, "ResurrectionException: a finaliser of %s kept a reference to the object it was finalising\n",
                t->name ? t->name : "?");
        arc_report();
        exit(70);
    }
    h->weak &= ~ARC_FLAG_FINALISING;
}

static void arc_destroy(ObjHeader* h) {
    size_t base, mark, i, j;
    if (g_in_teardown) {
        pending_push(h);
        return;
    }
    base = g_pending_len;
    pending_push(h);
    while (g_pending_len > base) {
        ObjHeader* o = g_pending[--g_pending_len];
        run_finalisers(o);
        mark = g_pending_len;
        g_in_teardown = 1;
        if (o->type && o->type->deinit) o->type->deinit(o);
        g_in_teardown = 0;
        for (i = mark, j = g_pending_len; i + 1 < j; i++, j--) {
            ObjHeader* swap = g_pending[i];
            g_pending[i] = g_pending[j - 1];
            g_pending[j - 1] = swap;
        }
        g_dead++;
        arc_weak_release(o);   /* drop the implicit live weak count */
    }
}

void arc_release(void* p) {
    if (!p) return;
    ObjHeader* h = (ObjHeader*)p;
    check_header("release", h, 1);
    g_release++;
    if (--h->strong == 0) arc_destroy(h);
}

/* GC.SuppressFinalize: skips the user finalisers at the last release. The
   field release and the reclamation still happen. */
void arc_suppress_finalize(void* p) {
    if (!p) return;
    ObjHeader* h = (ObjHeader*)p;
    check_header("suppress finalize", h, 1);
    h->weak |= ARC_FLAG_NO_FINALISE;
}

/* ---- slot assignment ------------------------------------------------- */
/* take ownership of an already-+1 value, releasing the previous occupant */
void arc_assign_take(void** slot, void* v) {
    void* old = *slot;
    *slot = v;
    if (old) arc_release(old);
}

/* general strong store: retains new, releases old */
void arc_store_strong(void** slot, void* v) {
    if (v) arc_retain(v);
    void* old = *slot;
    *slot = v;
    if (old) arc_release(old);
}

/* weak store: weak-retains new, weak-releases old */
void arc_store_weak(void** slot, void* v) {
    if (v) arc_weak_retain(v);
    void* old = *slot;
    *slot = v;
    if (old) arc_weak_release(old);
}

/* load a weak reference; returns a +1 strong reference or NULL if dead */
void* arc_load_weak(void** slot) {
    ObjHeader* h = (ObjHeader*)*slot;
    if (!h) return 0;
    check_header("weak load", h, 0);
    /* The object is logically destroyed at zero, and also while its
       finalisers run, when the count is held at one only to balance them. */
    if (h->strong <= 0 || (h->weak & ARC_FLAG_FINALISING)) return 0;
    h->strong++;
    g_retain++;
    return h;
}

/* ---- delegates -------------------------------------------------------- */
/* A delegate is an immutable list of nodes. Each node has the layout the
   emitter gives a delegate type: the target at offset 24, the function at 32,
   and the next node at 40. A node is never changed after it is built, so a
   list can share its tail with another list. Combining and removing build new
   lists and leave the old ones alone. Every function here returns an owned (+1)
   reference, or null for an empty list. */
typedef struct DelegateNode {
    ObjHeader            header;
    void*                target;   /* owned (+1) reference, or null for a static method */
    intptr_t             function;
    struct DelegateNode* next;     /* owned (+1) reference */
} DelegateNode;

static DelegateNode** delegate_nodes(DelegateNode* list, size_t* count) {
    size_t n = 0, i = 0;
    DelegateNode* node;
    DelegateNode** nodes;
    for (node = list; node; node = node->next) n++;
    nodes = (DelegateNode**)malloc((n ? n : 1) * sizeof(DelegateNode*));
    if (!nodes) {
        fprintf(stderr, "OutOfMemoryException: cannot read an invocation list\n");
        exit(70);
    }
    for (node = list; node; node = node->next) nodes[i++] = node;
    *count = n;
    return nodes;
}

/* Copies nodes[0..count) in front of tail, which is an owned reference that
   the new list takes over. */
static DelegateNode* delegate_copy_onto(DelegateNode** nodes, size_t count, DelegateNode* tail) {
    while (count > 0) {
        DelegateNode* source = nodes[--count];
        DelegateNode* copy = (DelegateNode*)arc_alloc(source->header.type, 0);
        arc_retain(source->target);
        copy->target = source->target;
        copy->function = source->function;
        copy->next = tail;
        tail = copy;
    }
    return tail;
}

#define ARC_SLOT_LOCAL_FUNCTION_CAPTURE 1

static int delegate_same_node(const DelegateNode* a, const DelegateNode* b) {
    if (a->function != b->function) return 0;
    if (a->target == b->target) return 1;
    if (!a->target || !b->target) return 0;

    /* Separate conversions can build separate environments for the same
       local function. The captured cells and receiver identify the target. */
    TypeInfo* type = ((ObjHeader*)a->target)->type;
    if (type != ((ObjHeader*)b->target)->type || type->slotCount == 0 ||
        !(type->slots[0].flags & ARC_SLOT_LOCAL_FUNCTION_CAPTURE)) return 0;
    for (intptr_t i = 0; i < type->slotCount; i++) {
        intptr_t offset = type->slots[i].offset;
        if (*(void**)((char*)a->target + offset) != *(void**)((char*)b->target + offset)) return 0;
    }
    return 1;
}

void* arc_delegate_combine(void* first, void* second) {
    size_t count;
    DelegateNode** nodes;
    DelegateNode* result;
    if (!first) { arc_retain(second); return second; }
    if (!second) { arc_retain(first); return first; }
    nodes = delegate_nodes((DelegateNode*)first, &count);
    arc_retain(second);
    result = delegate_copy_onto(nodes, count, (DelegateNode*)second);
    free(nodes);
    return result;
}

/* Removes the last occurrence of the value's list from the source's list. A
   source that does not contain it is returned as it is. */
void* arc_delegate_remove(void* source, void* value) {
    size_t sourceCount, valueCount, start, k;
    DelegateNode** sourceNodes;
    DelegateNode** valueNodes;
    DelegateNode* tail;
    DelegateNode* result;
    int found = 0;
    if (!source || !value) { arc_retain(source); return source; }
    sourceNodes = delegate_nodes((DelegateNode*)source, &sourceCount);
    valueNodes = delegate_nodes((DelegateNode*)value, &valueCount);
    start = valueCount <= sourceCount ? sourceCount - valueCount + 1 : 0;
    while (!found && start > 0) {
        start--;
        found = 1;
        for (k = 0; k < valueCount && found; k++) {
            found = delegate_same_node(sourceNodes[start + k], valueNodes[k]);
        }
    }
    if (!found) {
        arc_retain(source);
        result = (DelegateNode*)source;
    } else {
        /* The nodes after the removed run are untouched, so the new list shares them. */
        tail = start + valueCount < sourceCount ? sourceNodes[start + valueCount] : 0;
        arc_retain(tail);
        result = delegate_copy_onto(sourceNodes, start, tail);
    }
    free(sourceNodes);
    free(valueNodes);
    return result;
}

int arc_delegate_equal(void* a, void* b) {
    DelegateNode* x = (DelegateNode*)a;
    DelegateNode* y = (DelegateNode*)b;
    while (x && y) {
        if (x == y) return 1;
        if (!delegate_same_node(x, y)) return 0;
        x = x->next;
        y = y->next;
    }
    return x == y;
}

/* ---- arrays ---------------------------------------------------------- */
static void arc_array_ref_deinit(void* p) {
    intptr_t len = *(intptr_t*)((char*)p + 24);
    void** elems = (void**)((char*)p + 40);
    for (intptr_t i = 0; i < len; i++)
        if (elems[i]) arc_release(elems[i]);
}
static void arc_array_val_deinit(void* p) { (void)p; }

static TypeInfo* arc_array_elem_type(void* p);

static intptr_t arc_struct_stride(TypeInfo* elem) {
    return elem->instanceSize > 8 ? elem->instanceSize : 8;
}

static void arc_array_struct_deinit(void* p) {
    TypeInfo* elem = arc_array_elem_type(p);
    intptr_t stride = arc_struct_stride(elem);
    intptr_t length = *(intptr_t*)((char*)p + 24);
    char* elements = (char*)p + 40;
    for (intptr_t i = 0; i < length; i++) {
        elem->deinit(elements + i * stride);
    }
}

static TypeInfo arc_array_ref_ti = { "<ref[]>", 0, arc_array_ref_deinit, 0, 0, 0, 0 };
static TypeInfo arc_array_val_ti = { "<val[]>", 0, arc_array_val_deinit, 0, 0, 0, 0 };
static TypeInfo arc_array_struct_ti = { "<struct[]>", 0, arc_array_struct_deinit, 0, 0, 0, 0 };

enum { ARC_ARRAY_VALUE, ARC_ARRAY_REFERENCE, ARC_ARRAY_STRUCT };

void* arc_array_new(intptr_t length, int elementKind, TypeInfo* elemType, const char* site) {
    intptr_t stride = elementKind == ARC_ARRAY_STRUCT ? arc_struct_stride(elemType) : 8;
    intptr_t size = 40 + stride * (length < 0 ? 0 : length);
    ObjHeader* h = (ObjHeader*)calloc(1, (size_t)size);
    h->strong = 1; h->weak = 1;
    h->type = elementKind == ARC_ARRAY_STRUCT ? &arc_array_struct_ti
        : elementKind == ARC_ARRAY_REFERENCE ? &arc_array_ref_ti : &arc_array_val_ti;
    *(intptr_t*)((char*)h + 24) = length;
    *(TypeInfo**)((char*)h + 32) = elemType;
    g_alloc++;
    track(h, site);
    return h;
}
intptr_t arc_array_length(void* p) { return p ? *(intptr_t*)((char*)p + 24) : 0; }
static TypeInfo* arc_array_elem_type(void* p) { return p ? *(TypeInfo**)((char*)p + 32) : 0; }

void* arc_array_slice_new(void* source, intptr_t length, const char* site) {
    TypeInfo* arrayType = ((ObjHeader*)source)->type;
    int kind = arrayType == &arc_array_struct_ti ? ARC_ARRAY_STRUCT
        : arrayType == &arc_array_ref_ti ? ARC_ARRAY_REFERENCE : ARC_ARRAY_VALUE;
    return arc_array_new(length, kind, arc_array_elem_type(source), site);
}

/* Raises NullReferenceException or IndexOutOfRangeException and returns 0 when
   the access is not valid. The compiled code checks arc_exception afterwards. */
static int arc_in_bounds(void* arr, intptr_t i) {
    if (!arr) {
        arc_raise(ARC_EXC_NULL_REFERENCE, "Object reference not set to an instance of an object.");
        return 0;
    }
    if (i < 0 || i >= arc_array_length(arr)) {
        arc_raise(ARC_EXC_INDEX_OUT_OF_RANGE, "Index was outside the bounds of the array.");
        return 0;
    }
    return 1;
}

void arc_bounds_check(void* arr, intptr_t i) {
    arc_in_bounds(arr, i);
}

void arc_array_store_check(void* arr, intptr_t i, void* value) {
    TypeInfo* elem;
    if (!arc_in_bounds(arr, i)) return;
    elem = arc_array_elem_type(arr);
    if (!value || !elem) return;
    if (!arc_is_instance(value, elem)) {
        arc_raise(ARC_EXC_ARRAY_TYPE_MISMATCH,
                  "Attempted to access an element as a type incompatible with the array.");
    }
}

/* ---- strings --------------------------------------------------------- */
static void arc_str_deinit(void* p) { (void)p; }
static TypeInfo arc_str_ti = { "string", 0, arc_str_deinit, 0, 0, 0, 0 };

static uint16_t* arc_str_data(void* s) { return (uint16_t*)((char*)s + 32); }

static void* arc_str_alloc(intptr_t len, const char* site) {
    if (len > INT32_MAX) {
        fprintf(stderr, "OutOfMemoryException: string is too long\n");
        exit(70);
    }
    ObjHeader* h = (ObjHeader*)calloc(1, 32 + (size_t)len * sizeof(uint16_t));
    if (!h) {
        fprintf(stderr, "OutOfMemoryException: cannot allocate a string\n");
        exit(70);
    }
    h->strong = 1; h->weak = 1; h->type = &arc_str_ti;
    *(intptr_t*)((char*)h + 24) = len;
    g_alloc++;
    track(h, site);
    return h;
}

void* arc_str_literal(const uint16_t* data, intptr_t len, const char* site) {
    void* result = arc_str_alloc(len, site);
    if (len) memcpy(arc_str_data(result), data, (size_t)len * sizeof(uint16_t));
    return result;
}

static intptr_t arc_utf8_decode(const unsigned char* data, intptr_t len, uint16_t* output) {
    intptr_t count = 0;
    for (intptr_t i = 0; i < len;) {
        unsigned int scalar = data[i++];
        if (scalar >= 0x80) {
            int more = scalar >= 0xF0 ? 3 : scalar >= 0xE0 ? 2 : scalar >= 0xC2 ? 1 : 0;
            unsigned int min = more == 3 ? 0x10000 : more == 2 ? 0x800 : 0x80;
            scalar = more ? scalar & (0x7F >> (more + 1)) : 0xFFFD;
            for (int j = 0; j < more; j++) {
                if (i >= len || (data[i] & 0xC0) != 0x80) {
                    scalar = 0xFFFD;
                    break;
                }
                scalar = (scalar << 6) | (data[i++] & 0x3F);
            }
            if (scalar < min || scalar > 0x10FFFF || (scalar >= 0xD800 && scalar <= 0xDFFF))
                scalar = 0xFFFD;
        }
        if (scalar > 0xFFFF) {
            scalar -= 0x10000;
            if (output) output[count] = (uint16_t)(0xD800 + (scalar >> 10));
            count++;
            scalar = 0xDC00 + (scalar & 0x3FF);
        }
        if (output) output[count] = (uint16_t)scalar;
        count++;
    }
    return count;
}

static void* arc_str_new(const char* data, intptr_t len, const char* site) {
    intptr_t count = arc_utf8_decode((const unsigned char*)data, len, NULL);
    void* result = arc_str_alloc(count, site);
    arc_utf8_decode((const unsigned char*)data, len, arc_str_data(result));
    return result;
}

void* arc_str_lit(const char* data, intptr_t len, const char* site) { return arc_str_new(data, len, site); }
intptr_t arc_str_length(void* s) { return s ? *(intptr_t*)((char*)s + 24) : 0; }

int arc_str_equal(void* a, void* b) {
    if (a == b) return 1;
    if (!a || !b) return 0;
    intptr_t len = arc_str_length(a);
    return len == arc_str_length(b) && memcmp(arc_str_data(a), arc_str_data(b), (size_t)len * sizeof(uint16_t)) == 0;
}

// FNV-1a over the code units. A null string hashes to zero, which is the hash
// the collections give a null element.
int arc_str_hash(void* s) {
    if (!s) return 0;
    intptr_t length = arc_str_length(s);
    const uint16_t* data = arc_str_data(s);
    uint32_t hash = 2166136261u;
    for (intptr_t i = 0; i < length; i++) {
        hash ^= data[i];
        hash *= 16777619u;
    }
    return (int)hash;
}

// Objects do not move, so the address identifies an object for its lifetime.
// The shifts drop low bits, which vary little between allocations.
int arc_identity_hash(void* p) {
    uintptr_t address = (uintptr_t)p;
    return (int)((address >> 4) ^ (address >> 32));
}

void* arc_str_concat(void* a, void* b) {
    intptr_t la = arc_str_length(a), lb = arc_str_length(b);
    void* r = arc_str_alloc(la + lb, "<runtime>");
    uint16_t* buf = arc_str_data(r);
    if (la) memcpy(buf, arc_str_data(a), (size_t)la * sizeof(uint16_t));
    if (lb) memcpy(buf + la, arc_str_data(b), (size_t)lb * sizeof(uint16_t));
    return r;
}

int arc_str_index(void* s, int index) {
    if (index < 0 || index >= arc_str_length(s)) {
        arc_raise(ARC_EXC_INDEX_OUT_OF_RANGE, "Index was outside the bounds of the string.");
        return 0;
    }
    return arc_str_data(s)[index];
}

void* arc_str_substring(void* s, int start, int count, int has_count) {
    intptr_t length = arc_str_length(s);
    if (start < 0 || start > length || (has_count && (count < 0 || count > length - start))) {
        arc_raise(ARC_EXC_ARGUMENT_OUT_OF_RANGE, "Substring bounds were outside the string.");
        return NULL;
    }
    intptr_t units = has_count ? count : length - start;
    if (units == length) {
        arc_retain(s);
        return s;
    }
    return arc_str_literal(arc_str_data(s) + start, units, "<runtime>");
}

int arc_str_find(void* s, void* value, int mode) {
    if (!value) {
        arc_raise(ARC_EXC_ARGUMENT_NULL, "Value cannot be null.");
        return -1;
    }
    intptr_t count = arc_str_length(value);
    intptr_t last = arc_str_length(s) - count;
    if (last < 0) return -1;
    intptr_t first = mode == 2 ? last : 0;
    if (mode == 1) last = 0;
    for (intptr_t i = first; i <= last; i++) {
        if (!count || memcmp(arc_str_data(s) + i, arc_str_data(value), (size_t)count * sizeof(uint16_t)) == 0)
            return (int)i;
    }
    return -1;
}

int arc_str_find_char(void* s, int value) {
    intptr_t length = arc_str_length(s);
    const uint16_t* data = arc_str_data(s);
    for (intptr_t i = 0; i < length; i++) {
        if (data[i] == value) return (int)i;
    }
    return -1;
}

static int arc_str_is_space(uint16_t unit) {
    return (unit >= 0x9 && unit <= 0xD) || unit == 0x20 || unit == 0x85 || unit == 0xA0
        || unit == 0x1680 || (unit >= 0x2000 && unit <= 0x200A) || unit == 0x2028
        || unit == 0x2029 || unit == 0x202F || unit == 0x205F || unit == 0x3000;
}

int arc_str_is_blank(void* s) {
    intptr_t length = arc_str_length(s);
    for (intptr_t i = 0; i < length; i++) {
        if (!arc_str_is_space(arc_str_data(s)[i])) return 0;
    }
    return 1;
}

void* arc_str_trim(void* s) {
    const uint16_t* data = arc_str_data(s);
    intptr_t start = 0, end = arc_str_length(s);
    while (start < end && arc_str_is_space(data[start])) start++;
    while (end > start && arc_str_is_space(data[end - 1])) end--;
    return arc_str_substring(s, (int)start, (int)(end - start), 1);
}

void* arc_str_replace_char(void* s, int before, int after) {
    int first = before == after ? -1 : arc_str_find_char(s, before);
    if (first < 0) {
        arc_retain(s);
        return s;
    }
    intptr_t length = arc_str_length(s);
    void* result = arc_str_literal(arc_str_data(s), length, "<runtime>");
    uint16_t* data = arc_str_data(result);
    for (intptr_t i = first; i < length; i++) {
        if (data[i] == before) data[i] = (uint16_t)after;
    }
    return result;
}

void* arc_str_replace(void* s, void* before, void* after) {
    if (!before) {
        arc_raise(ARC_EXC_ARGUMENT_NULL, "Value cannot be null.");
        return NULL;
    }
    intptr_t from = arc_str_length(before);
    if (!from) {
        arc_raise(ARC_EXC_ARGUMENT, "The value cannot be an empty string.");
        return NULL;
    }
    intptr_t length = arc_str_length(s);
    intptr_t to = arc_str_length(after);
    const uint16_t* source = arc_str_data(s);
    const uint16_t* needle = arc_str_data(before);
    size_t match_bytes = (size_t)from * sizeof(uint16_t);
    intptr_t matches = 0;
    for (intptr_t i = 0; i <= length - from;) {
        if (memcmp(source + i, needle, match_bytes) == 0) {
            matches++;
            i += from;
        } else {
            i++;
        }
    }
    if (!matches) {
        arc_retain(s);
        return s;
    }
    void* result = arc_str_alloc(length + matches * (to - from), "<runtime>");
    uint16_t* dest = arc_str_data(result);
    for (intptr_t i = 0; i < length;) {
        if (i <= length - from && memcmp(source + i, needle, match_bytes) == 0) {
            if (to) memcpy(dest, arc_str_data(after), (size_t)to * sizeof(uint16_t));
            dest += to;
            i += from;
        } else {
            *dest++ = source[i++];
        }
    }
    return result;
}

void* arc_str_newline(void) {
#ifdef _WIN32
    return arc_str_new("\r\n", 2, "<runtime>");
#else
    return arc_str_new("\n", 1, "<runtime>");
#endif
}

void* arc_str_from_int(intptr_t v) {
    char tmp[32];
    int n = snprintf(tmp, sizeof tmp, "%lld", (long long)v);
    return arc_str_new(tmp, n, "<runtime>");
}
void* arc_str_from_uint(uint64_t v) {
    char tmp[32];
    int n = snprintf(tmp, sizeof tmp, "%llu", (unsigned long long)v);
    return arc_str_new(tmp, n, "<runtime>");
}
/* Encode a Unicode scalar for console output. */
static int arc_utf8_unit(unsigned int u, unsigned char* out) {
    if (u <= 0x7F) {
        out[0] = (unsigned char)u;
        return 1;
    }
    if (u <= 0x7FF) {
        out[0] = (unsigned char)(0xC0 | (u >> 6));
        out[1] = (unsigned char)(0x80 | (u & 0x3F));
        return 2;
    }
    if (u <= 0xFFFF && (u < 0xD800 || u > 0xDFFF)) {
        out[0] = (unsigned char)(0xE0 | (u >> 12));
        out[1] = (unsigned char)(0x80 | ((u >> 6) & 0x3F));
        out[2] = (unsigned char)(0x80 | (u & 0x3F));
        return 3;
    }
    if (u >= 0x10000 && u <= 0x10FFFF) {
        out[0] = (unsigned char)(0xF0 | (u >> 18));
        out[1] = (unsigned char)(0x80 | ((u >> 12) & 0x3F));
        out[2] = (unsigned char)(0x80 | ((u >> 6) & 0x3F));
        out[3] = (unsigned char)(0x80 | (u & 0x3F));
        return 4;
    }
    out[0] = '?';
    return 1;
}
void* arc_str_from_char(int c) {
    uint16_t unit = (uint16_t)c;
    return arc_str_literal(&unit, 1, "<runtime>");
}
void* arc_str_from_bool(int v) {
    return v ? arc_str_new("True", 4, "<runtime>") : arc_str_new("False", 5, "<runtime>");
}
void* arc_str_from_float(float v) {
    char tmp[64];
    int n = snprintf(tmp, sizeof tmp, "%g", (double)v);
    return arc_str_new(tmp, n, "<runtime>");
}
void* arc_str_from_double(double v) {
    char tmp[64];
    int n = snprintf(tmp, sizeof tmp, "%g", v);
    return arc_str_new(tmp, n, "<runtime>");
}

void* arc_str_align(void* s, int width) {
    intptr_t wanted = width < 0 ? -(intptr_t)width : width;
    intptr_t length = arc_str_length(s);
    if (length >= wanted) {
        arc_retain(s);
        return s;
    }
    void* result = arc_str_alloc(wanted, "<runtime>");
    uint16_t* data = arc_str_data(result);
    for (intptr_t i = 0; i < wanted; i++) data[i] = ' ';
    if (length)
        memcpy(data + (width < 0 ? 0 : wanted - length), arc_str_data(s), (size_t)length * sizeof(uint16_t));
    return result;
}

static int arc_str_format_spec(void* format, const char* supported, int* precision) {
    *precision = -1;
    intptr_t length = arc_str_length(format);
    if (!length) return 'G';
    const uint16_t* data = arc_str_data(format);
    int kind = data[0];
    if (!kind || kind > 127 || !strchr(supported, kind)) return 0;
    if (length > 1) {
        *precision = 0;
        for (intptr_t i = 1; i < length; i++) {
            if (data[i] < '0' || data[i] > '9' || *precision > 9) return 0;
            *precision = *precision * 10 + data[i] - '0';
        }
    }
    return kind;
}

static void* arc_str_bad_format(void) {
    arc_raise(ARC_EXC_FORMAT, "The numeric format specifier is not supported.");
    return NULL;
}

static void* arc_str_format_int(uint64_t value, int width, void* format) {
    int precision;
    int kind = arc_str_format_spec(format, "DdGgXx", &precision);
    if (!kind || ((kind == 'g' || kind == 'G') && precision > 0)) return arc_str_bad_format();
    char text[104];
    int count;
    if (kind == 'x' || kind == 'X') {
        int bits = width < 0 ? -width : width;
        if (bits != 64) value &= (UINT64_C(1) << bits) - 1;
        count = snprintf(text, sizeof text, kind == 'x' ? "%.*llx" : "%.*llX",
            precision > 0 ? precision : 1, (unsigned long long)value);
    } else {
        int negative = width < 0 && (value >> 63);
        uint64_t magnitude = negative ? UINT64_C(0) - value : value;
        int digits = (kind == 'd' || kind == 'D') && precision > 0 ? precision : 1;
        count = snprintf(text, sizeof text, "%s%.*llu", negative ? "-" : "", digits, (unsigned long long)magnitude);
    }
    return arc_str_new(text, count, "<runtime>");
}

static void* arc_str_format_real(double value, int single, void* format) {
    int precision;
    int kind = arc_str_format_spec(format, "FfEeGg", &precision);
    if (!kind) return arc_str_bad_format();
    if (isnan(value)) return arc_str_new("NaN", 3, "<runtime>");
    if (isinf(value)) return value < 0 ? arc_str_new("-Infinity", 9, "<runtime>") : arc_str_new("Infinity", 8, "<runtime>");
    char text[512];
    int count;
    if (kind == 'F' || kind == 'f') {
        count = snprintf(text, sizeof text, "%.*f", precision < 0 ? 2 : precision, value);
    } else if (kind == 'E' || kind == 'e') {
        count = snprintf(text, sizeof text, kind == 'E' ? "%.*E" : "%.*e", precision < 0 ? 6 : precision, value);
        char* exponent = strchr(text, kind);
        if (exponent && strlen(exponent) == 4) {
            memmove(exponent + 3, exponent + 2, 3);
            exponent[2] = '0';
            count++;
        }
    } else if (precision > 0) {
        count = snprintf(text, sizeof text, kind == 'G' ? "%.*G" : "%.*g", precision, value);
    } else {
        int limit = single ? 9 : 17;
        for (int digits = 1;; digits++) {
            count = snprintf(text, sizeof text, kind == 'G' ? "%.*G" : "%.*g", digits, value);
            double parsed = strtod(text, NULL);
            if ((single ? (float)parsed == (float)value : parsed == value) || digits == limit) break;
        }
    }
    return arc_str_new(text, count, "<runtime>");
}

/* Each format argument is a payload followed by its representation tag.
   Tags +/-8 through +/-64 encode signedness and integer width. */
void* arc_str_format_value(uint64_t value, int kind, void* format) {
    if (kind == 0) {
        void* text = (void*)(uintptr_t)value;
        arc_retain(text);
        return text;
    }
    if (kind == 1 || kind == 2) {
        double real;
        memcpy(&real, &value, sizeof real);
        return arc_str_format_real(real, kind == 1, format);
    }
    return arc_str_format_int(value, kind, format);
}

typedef struct ArcTextBuffer {
    uint16_t* data;
    intptr_t length;
    intptr_t capacity;
} ArcTextBuffer;

static void arc_str_buffer_add(ArcTextBuffer* buffer, const uint16_t* data, intptr_t count) {
    if (!count) return;
    intptr_t total = buffer->length + count;
    if (total > buffer->capacity) {
        intptr_t capacity = buffer->capacity + buffer->capacity / 2 + 16;
        if (capacity < total) capacity = total;
        uint16_t* grown = (uint16_t*)realloc(buffer->data, (size_t)capacity * sizeof(uint16_t));
        if (!grown) {
            fprintf(stderr, "OutOfMemoryException: cannot grow a formatting buffer\n");
            exit(70);
        }
        buffer->data = grown;
        buffer->capacity = capacity;
    }
    memcpy(buffer->data + buffer->length, data, (size_t)count * sizeof(uint16_t));
    buffer->length = total;
}

static int arc_str_read_number(const uint16_t** cursor, const uint16_t* end, int* number) {
    const uint16_t* p = *cursor;
    if (p == end || *p < '0' || *p > '9') return 0;
    int value = 0;
    do {
        int digit = *p++ - '0';
        if (value > (INT32_MAX - digit) / 10) return 0;
        value = value * 10 + digit;
    } while (p < end && *p >= '0' && *p <= '9');
    *cursor = p;
    *number = value;
    return 1;
}

void* arc_str_composite(void* format, const int64_t* arguments, intptr_t count) {
    if (!format) {
        arc_raise(ARC_EXC_ARGUMENT_NULL, "Value cannot be null.");
        return NULL;
    }
    const uint16_t* p = arc_str_data(format);
    const uint16_t* end = p + arc_str_length(format);
    ArcTextBuffer buffer = { 0 };
    while (p < end) {
        uint16_t unit = *p++;
        if (unit != '{' && unit != '}') {
            arc_str_buffer_add(&buffer, &unit, 1);
            continue;
        }
        if (p < end && *p == unit) {
            p++;
            arc_str_buffer_add(&buffer, &unit, 1);
            continue;
        }

        int index, width = 0;
        if (unit != '{' || !arc_str_read_number(&p, end, &index) || index >= count) goto invalid;
        while (p < end && *p == ' ') p++;
        if (p < end && *p == ',') {
            p++;
            while (p < end && *p == ' ') p++;
            int left = p < end && *p == '-';
            if (left) p++;
            if (!arc_str_read_number(&p, end, &width)) goto invalid;
            if (left) width = -width;
            while (p < end && *p == ' ') p++;
        }

        const uint16_t* spec = p;
        if (p < end && *p == ':') {
            spec = ++p;
            while (p < end && *p != '}' && *p != '{') p++;
        }
        if (p == end || *p != '}') goto invalid;
        int kind = (int)arguments[2 * (intptr_t)index + 1];
        void* pattern = kind && p != spec ? arc_str_literal(spec, p - spec, "<runtime>") : NULL;
        void* value = arc_str_format_value((uint64_t)arguments[2 * (intptr_t)index], kind, pattern);
        arc_release(pattern);
        if (arc_exception) {
            free(buffer.data);
            return NULL;
        }
        void* field = arc_str_align(value, width);
        arc_release(value);
        if (field) arc_str_buffer_add(&buffer, arc_str_data(field), arc_str_length(field));
        arc_release(field);
        p++;
    }
    void* result = arc_str_literal(buffer.data, buffer.length, "<runtime>");
    free(buffer.data);
    return result;

invalid:
    free(buffer.data);
    arc_raise(ARC_EXC_FORMAT, "Input string was not in a correct format.");
    return NULL;
}

/* ---- WeakReference<T> ----------------------------------------------- */
/* A small ARC object whose single 8-byte slot (offset 24) holds a weak ref. */
static void arc_weakref_deinit(void* p) {
    void** slot = (void**)((char*)p + 24);
    arc_store_weak(slot, 0);           /* weak-release the target */
}
static TypeInfo arc_weakref_ti = { "WeakReference", 32, arc_weakref_deinit, 0, 0, 0, 0 };

void* arc_weakref_new(void* target, const char* site) {
    ObjHeader* h = (ObjHeader*)calloc(1, 32);
    h->strong = 1; h->weak = 1; h->type = &arc_weakref_ti;
    void** slot = (void**)((char*)h + 24);
    arc_store_weak(slot, target);      /* weak-retain target */
    g_alloc++;
    track(h, site);
    return h;
}
void* arc_weakref_try_get(void* wr) {
    if (!wr) return 0;
    check_header("weak load", (ObjHeader*)wr, 1);
    void** slot = (void**)((char*)wr + 24);
    return arc_load_weak(slot);        /* +1 strong, or NULL if target died */
}
int arc_is_instance(void* obj, TypeInfo* target) {
    if (!obj || !target) return 0;
    TypeInfo* cur = ((ObjHeader*)obj)->type;
    while (cur) {
        if (cur == target) return 1;
        cur = cur->base;
    }
    return 0;
}
int arc_has_interface(void* obj, intptr_t slot) {
    if (!obj || slot < 0) return 0;
    TypeInfo* type = ((ObjHeader*)obj)->type;
    return type && slot < type->itableLen && type->itable[slot] != NULL;
}
void arc_weakref_set(void* wr, void* target) {
    if (!wr) return;
    void** slot = (void**)((char*)wr + 24);
    arc_store_weak(slot, target);
}

/* ---- leak report (ARC_LEAKS=1) --------------------------------------- */
/* Reads the strong graph and prints it. Does not retain, release, or free. */

typedef struct LeakObj {
    ObjHeader* obj;
    const char* site;
    int from_root;
    int color;
} LeakObj;

typedef struct LeakWalk {
    LeakObj* objs;
    int n;
    int* stack;
    int top;
    char** seen;
    int seen_count;
} LeakWalk;

static const char* leak_type_name(ObjHeader* h) {
    if (h->type && h->type->name && h->type->name[0]) return h->type->name;
    return "?";
}

static int leak_find(LeakObj* objs, int n, void* p) {
    int i;
    for (i = 0; i < n; i++) {
        if (objs[i].obj == p) return i;
    }
    return -1;
}

static void leak_each(ObjHeader* h, void (*fn)(void*, void*), void* ctx);

static void leak_slots(char* base, ArcSlot* slots, intptr_t n, void (*fn)(void*, void*), void* ctx) {
    intptr_t i;
    if (!slots) return;
    for (i = 0; i < n; i++) {
        ArcSlot* s = &slots[i];
        if (s->kind == 1) {
            void* p = *(void**)(base + s->offset);
            if (p) fn(p, ctx);
        } else if (s->kind == 2 && s->nested) {
            leak_slots(base + s->offset, s->nested->slots, s->nested->slotCount, fn, ctx);
        }
    }
}

static void leak_each(ObjHeader* h, void (*fn)(void*, void*), void* ctx) {
    TypeInfo* t;
    if (!h) return;
    t = h->type;
    if (!t) return;
    if (t == &arc_array_ref_ti) {
        intptr_t len = *(intptr_t*)((char*)h + 24);
        void** elems = (void**)((char*)h + 40);
        intptr_t i;
        for (i = 0; i < len; i++) {
            if (elems[i]) fn(elems[i], ctx);
        }
        return;
    }
    if (t == &arc_array_struct_ti) {
        TypeInfo* elem = arc_array_elem_type(h);
        intptr_t stride = arc_struct_stride(elem);
        intptr_t length = arc_array_length(h);
        char* elements = (char*)h + 40;
        for (intptr_t i = 0; i < length; i++) {
            leak_slots(elements + i * stride, elem->slots, elem->slotCount, fn, ctx);
        }
        return;
    }
    leak_slots((char*)h, t->slots, t->slotCount, fn, ctx);
}

static void leak_mark(void* p, void* ctx) {
    LeakWalk* w = (LeakWalk*)ctx;
    int i = leak_find(w->objs, w->n, p);
    if (i < 0 || w->objs[i].from_root) return;
    w->objs[i].from_root = 1;
    leak_each(w->objs[i].obj, leak_mark, w);
}

static int leak_seen(LeakWalk* w, const char* key) {
    int i;
    for (i = 0; i < w->seen_count; i++) {
        if (strcmp(w->seen[i], key) == 0) return 1;
    }
    return 0;
}

static void leak_report_cycle(LeakWalk* w, int back) {
    int start, len, best, i;
    int* path;
    char* key;
    size_t used, cap;
    for (start = 0; start < w->top; start++) {
        if (w->stack[start] == back) break;
    }
    if (start == w->top) return;
    len = w->top - start;
    path = (int*)malloc((size_t)len * sizeof(int));
    if (!path) return;
    best = 0;
    for (i = 0; i < len; i++) {
        path[i] = w->stack[start + i];
        if ((uintptr_t)w->objs[path[i]].obj < (uintptr_t)w->objs[path[best]].obj) best = i;
    }
    cap = (size_t)len * 24 + 1;
    key = (char*)malloc(cap);
    if (!key) {
        free(path);
        return;
    }
    key[0] = 0;
    used = 0;
    for (i = 0; i < len; i++) {
        int idx = path[(best + i) % len];
        char buf[32];
        int n = snprintf(buf, sizeof buf, "%s%llx", i ? "," : "",
                         (unsigned long long)(uintptr_t)w->objs[idx].obj);
        if (n < 0 || used + (size_t)n >= cap) break;
        memcpy(key + used, buf, (size_t)n);
        used += (size_t)n;
        key[used] = 0;
    }
    if (!leak_seen(w, key)) {
        char** grown = (char**)realloc(w->seen, (size_t)(w->seen_count + 1) * sizeof(char*));
        if (grown) {
            w->seen = grown;
            w->seen[w->seen_count++] = key;
            key = 0;
        }
        fprintf(stderr, "[arc] cycle ");
        for (i = 0; i < len; i++) {
            int idx = path[(best + i) % len];
            if (i) fprintf(stderr, " -> ");
            fprintf(stderr, "%s", leak_type_name(w->objs[idx].obj));
        }
        fprintf(stderr, " -> %s\n", leak_type_name(w->objs[path[best]].obj));
    }
    free(key);
    free(path);
}

static void leak_consider(void* p, void* ctx);

static void leak_dfs(LeakWalk* w, int u) {
    w->objs[u].color = 1;
    w->stack[w->top++] = u;
    leak_each(w->objs[u].obj, leak_consider, w);
    w->top--;
    w->objs[u].color = 2;
}

static void leak_consider(void* p, void* ctx) {
    LeakWalk* w = (LeakWalk*)ctx;
    int j = leak_find(w->objs, w->n, p);
    if (j < 0 || w->objs[j].from_root) return;
    if (w->objs[j].color == 1) {
        leak_report_cycle(w, j);
        return;
    }
    if (w->objs[j].color == 0) leak_dfs(w, j);
}

static int leak_cmp(const void* a, const void* b) {
    const LeakObj* x = (const LeakObj*)a;
    const LeakObj* y = (const LeakObj*)b;
    int c = strcmp(leak_type_name(x->obj), leak_type_name(y->obj));
    if (c != 0) return c;
    c = strcmp(x->site ? x->site : "", y->site ? y->site : "");
    if (c != 0) return c;
    if ((uintptr_t)x->obj < (uintptr_t)y->obj) return -1;
    if ((uintptr_t)x->obj > (uintptr_t)y->obj) return 1;
    return 0;
}

static void arc_report_leaks(void) {
    int n = 0, i;
    ArcRec* r;
    LeakObj* objs;
    LeakWalk walk;
    for (r = g_recs; r; r = r->next) {
        if (r->obj && r->obj->strong > 0) n++;
    }
    if (n == 0) return;
    objs = (LeakObj*)calloc((size_t)n, sizeof(LeakObj));
    if (!objs) {
        fprintf(stderr, "[arc] leak detector could not allocate its report\n");
        return;
    }
    i = 0;
    for (r = g_recs; r; r = r->next) {
        if (r->obj && r->obj->strong > 0) {
            objs[i].obj = r->obj;
            objs[i].site = r->site ? r->site : "-";
            i++;
        }
    }
    n = i;
    qsort(objs, (size_t)n, sizeof(LeakObj), leak_cmp);
    walk.objs = objs;
    walk.n = n;
    walk.stack = (int*)calloc((size_t)n, sizeof(int));
    walk.top = 0;
    walk.seen = 0;
    walk.seen_count = 0;
    if (!walk.stack) {
        fprintf(stderr, "[arc] leak detector could not allocate its report\n");
        free(objs);
        return;
    }
    if (g_roots) {
        for (i = 0; i < g_root_count; i++) {
            void* root = g_roots[i] ? *g_roots[i] : 0;
            if (root) leak_mark(root, &walk);
        }
    }
    for (i = 0; i < n; i++) {
        ObjHeader* h = objs[i].obj;
        fprintf(stderr,
                "[arc] survivor addr=%p type=%s strong=%lld weak=%lld site=%s root=%s\n",
                (void*)h, leak_type_name(h), (long long)h->strong, (long long)WEAK_COUNT(h),
                objs[i].site, objs[i].from_root ? "static" : "unreachable");
    }
    for (i = 0; i < n; i++) {
        if (!objs[i].from_root && objs[i].color == 0) leak_dfs(&walk, i);
    }
    for (i = 0; i < walk.seen_count; i++) free(walk.seen[i]);
    free(walk.seen);
    free(walk.stack);
    free(objs);
}

/* ---- console --------------------------------------------------------- */
static void arc_str_write(FILE* stream, void* s) {
    intptr_t length = arc_str_length(s);
    for (intptr_t i = 0; i < length; i++) {
        const uint16_t* data = arc_str_data(s);
        unsigned int scalar = data[i];
        if (scalar >= 0xD800 && scalar <= 0xDBFF && i + 1 < length
            && data[i + 1] >= 0xDC00 && data[i + 1] <= 0xDFFF) {
            scalar = 0x10000 + ((scalar - 0xD800) << 10) + (data[++i] - 0xDC00);
        }
        unsigned char bytes[4];
        int count = arc_utf8_unit(scalar, bytes);
        fwrite(bytes, 1, (size_t)count, stream);
    }
}

void arc_console_write(void* s, int nl) {
    arc_str_write(stdout, s);
    if (nl) fputc('\n', stdout);
}
void arc_console_write_int(long long v, int nl) {
    printf("%lld", v);
    if (nl) putchar('\n');
}
void arc_console_write_uint(uint64_t v, int nl) {
    printf("%llu", (unsigned long long)v);
    if (nl) putchar('\n');
}
void arc_console_write_char(int c, int nl) {
    unsigned char tmp[4];
    int n = arc_utf8_unit((unsigned int)c, tmp);
    fwrite(tmp, 1, (size_t)n, stdout);
    if (nl) putchar('\n');
}
void arc_console_write_bool(int v, int nl) {
    fputs(v ? "True" : "False", stdout);
    if (nl) putchar('\n');
}
void arc_console_write_float(float v, int nl) {
    printf("%g", (double)v);
    if (nl) putchar('\n');
}
void arc_console_write_double(double v, int nl) {
    printf("%g", v);
    if (nl) putchar('\n');
}
void arc_console_newline(void) { putchar('\n'); }
