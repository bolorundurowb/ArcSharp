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
 *   offset 32 : string bytes, or the element TypeInfo* of an array
 *   offset 40 : array elements (one or more 8-byte slots per element)
 */
/* getenv is the portable way to read runtime options. The MSVC headers mark it
   deprecated in favour of _dupenv_s, which is not portable. */
#define _CRT_SECURE_NO_WARNINGS
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
    int32_t   pad;
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
    ARC_EXC_KIND_COUNT
};

#define ARC_EXC_MESSAGE_OFFSET 24

static TypeInfo** g_exc_types = 0;
static intptr_t g_exc_count = 0;

void arc_release(void* p);
static void* arc_str_new(const char* data, intptr_t len, const char* site);

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
        fprintf(stderr, "%s: %s\n", name, (const char*)message + 32);
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

static void* arc_str_new(const char* data, intptr_t len, const char* site) {
    intptr_t size = 32 + len + 1;
    ObjHeader* h = (ObjHeader*)calloc(1, (size_t)size);
    h->strong = 1; h->weak = 1; h->type = &arc_str_ti;
    *(intptr_t*)((char*)h + 24) = len;
    char* buf = (char*)h + 32;
    if (data) memcpy(buf, data, (size_t)len);
    buf[len] = 0;
    g_alloc++;
    track(h, site);
    return h;
}
static char* arc_str_data(void* s) { return (char*)s + 32; }

void* arc_str_lit(const char* data, intptr_t len, const char* site) { return arc_str_new(data, len, site); }
intptr_t arc_str_length(void* s) { return s ? *(intptr_t*)((char*)s + 24) : 0; }

int arc_str_equal(void* a, void* b) {
    if (a == b) return 1;
    if (!a || !b) return 0;
    intptr_t len = arc_str_length(a);
    return len == arc_str_length(b) && memcmp(arc_str_data(a), arc_str_data(b), (size_t)len) == 0;
}

void* arc_str_concat(void* a, void* b) {
    intptr_t la = arc_str_length(a), lb = arc_str_length(b);
    void* r = arc_str_new(0, la + lb, "<runtime>");
    char* buf = arc_str_data(r);
    if (a) memcpy(buf,        arc_str_data(a), (size_t)la);
    if (b) memcpy(buf + la,   arc_str_data(b), (size_t)lb);
    buf[la + lb] = 0;
    return r;
}
void* arc_str_from_int(intptr_t v) {
    char tmp[32];
    int n = snprintf(tmp, sizeof tmp, "%lld", (long long)v);
    return arc_str_new(tmp, n, "<runtime>");
}
/* A C# char is one UTF-16 code unit. Encode a scalar in the BMP; a surrogate
   has no character of its own. */
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
    out[0] = '?';
    return 1;
}
void* arc_str_from_char(int c) {
    unsigned char tmp[3];
    int n = arc_utf8_unit((unsigned int)c, tmp);
    return arc_str_new((const char*)tmp, n, "<runtime>");
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
void arc_console_write(void* s, int nl) {
    if (s) fputs(arc_str_data(s), stdout);
    if (nl) fputc('\n', stdout);
}
void arc_console_write_int(long long v, int nl) {
    printf("%lld", v);
    if (nl) putchar('\n');
}
void arc_console_write_char(int c, int nl) {
    unsigned char tmp[3];
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
