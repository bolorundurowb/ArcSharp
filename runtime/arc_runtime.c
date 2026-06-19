/*
 * ArcSharp runtime — Automatic Reference Counting support for the GC-less
 * executables produced by the ArcSharp compiler.
 *
 * Single-threaded; refcounts are non-atomic by design (see ARCHITECTURE.md).
 *
 * Object memory layout (all slots are 8 bytes; "uniform slot" model):
 *   offset  0 : intptr_t strong        reference count
 *   offset  8 : intptr_t weak          weak count (+1 while object is live)
 *   offset 16 : TypeInfo* type         type descriptor
 *   offset 24 : first field / array length / string length
 *   offset 32 : array elements / string bytes
 */
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

struct TypeInfo {
    const char* name;
    intptr_t    instanceSize;
    void      (*deinit)(void*);
    void**      vtable;
    intptr_t    vtableLen;
    void**      itable;
    intptr_t    itableLen;
};

/* ---- accounting (for verification / leak detection) ------------------ */
static long g_alloc = 0;   /* total objects allocated                    */
static long g_dead  = 0;   /* objects whose strong count reached 0        */
static long g_free  = 0;   /* allocations actually freed (weak hit 0)     */

void arc_report(void) {
    fprintf(stderr, "[arc] alloc=%ld dead=%ld freed=%ld live=%ld\n",
            g_alloc, g_dead, g_free, g_alloc - g_dead);
}
long arc_live_count(void) { return g_alloc - g_dead; }

/* ---- core retain / release ------------------------------------------- */
void* arc_alloc(TypeInfo* type) {
    ObjHeader* h = (ObjHeader*)calloc(1, (size_t)type->instanceSize);
    h->strong = 1;
    h->weak   = 1;
    h->type   = type;
    g_alloc++;
    return h;
}

void arc_retain(void* p) {
    if (p) ((ObjHeader*)p)->strong++;
}

void arc_weak_retain(void* p) {
    if (p) ((ObjHeader*)p)->weak++;
}

void arc_weak_release(void* p) {
    if (!p) return;
    ObjHeader* h = (ObjHeader*)p;
    if (--h->weak == 0) { free(h); g_free++; }
}

void arc_release(void* p) {
    if (!p) return;
    ObjHeader* h = (ObjHeader*)p;
    if (--h->strong == 0) {
        if (h->type && h->type->deinit) h->type->deinit(h);
        g_dead++;
        arc_weak_release(h);   /* drop the implicit live weak count */
    }
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
    if (h->strong <= 0) return 0;   /* object logically destroyed */
    h->strong++;
    return h;
}

/* ---- arrays ---------------------------------------------------------- */
static void arc_array_ref_deinit(void* p) {
    intptr_t len = *(intptr_t*)((char*)p + 24);
    void** elems = (void**)((char*)p + 32);
    for (intptr_t i = 0; i < len; i++)
        if (elems[i]) arc_release(elems[i]);
}
static void arc_array_val_deinit(void* p) { (void)p; }

static TypeInfo arc_array_ref_ti = { "<ref[]>", 0, arc_array_ref_deinit, 0, 0, 0, 0 };
static TypeInfo arc_array_val_ti = { "<val[]>", 0, arc_array_val_deinit, 0, 0, 0, 0 };

void* arc_array_new(intptr_t length, int isRef) {
    intptr_t size = 32 + 8 * (length < 0 ? 0 : length);
    ObjHeader* h = (ObjHeader*)calloc(1, (size_t)size);
    h->strong = 1; h->weak = 1;
    h->type = isRef ? &arc_array_ref_ti : &arc_array_val_ti;
    *(intptr_t*)((char*)h + 24) = length;
    g_alloc++;
    return h;
}
intptr_t arc_array_length(void* p) { return p ? *(intptr_t*)((char*)p + 24) : 0; }

void arc_bounds_check(void* arr, intptr_t i) {
    intptr_t len = arc_array_length(arr);
    if (i < 0 || i >= len) {
        fprintf(stderr, "IndexOutOfRangeException: index %ld, length %ld\n", (long)i, (long)len);
        arc_report();
        exit(70);
    }
}

/* ---- strings --------------------------------------------------------- */
static void arc_str_deinit(void* p) { (void)p; }
static TypeInfo arc_str_ti = { "string", 0, arc_str_deinit, 0, 0, 0, 0 };

static void* arc_str_new(const char* data, intptr_t len) {
    intptr_t size = 32 + len + 1;
    ObjHeader* h = (ObjHeader*)calloc(1, (size_t)size);
    h->strong = 1; h->weak = 1; h->type = &arc_str_ti;
    *(intptr_t*)((char*)h + 24) = len;
    char* buf = (char*)h + 32;
    if (data) memcpy(buf, data, (size_t)len);
    buf[len] = 0;
    g_alloc++;
    return h;
}
static char* arc_str_data(void* s) { return (char*)s + 32; }

void* arc_str_lit(const char* data, intptr_t len) { return arc_str_new(data, len); }
intptr_t arc_str_length(void* s) { return s ? *(intptr_t*)((char*)s + 24) : 0; }

void* arc_str_concat(void* a, void* b) {
    intptr_t la = arc_str_length(a), lb = arc_str_length(b);
    void* r = arc_str_new(0, la + lb);
    char* buf = arc_str_data(r);
    if (a) memcpy(buf,        arc_str_data(a), (size_t)la);
    if (b) memcpy(buf + la,   arc_str_data(b), (size_t)lb);
    buf[la + lb] = 0;
    return r;
}
void* arc_str_from_int(intptr_t v) {
    char tmp[32];
    int n = snprintf(tmp, sizeof tmp, "%ld", (long)v);
    return arc_str_new(tmp, n);
}
void* arc_str_from_bool(int v) {
    return v ? arc_str_new("True", 4) : arc_str_new("False", 5);
}

/* ---- WeakReference<T> ----------------------------------------------- */
/* A small ARC object whose single 8-byte slot (offset 24) holds a weak ref. */
static void arc_weakref_deinit(void* p) {
    void** slot = (void**)((char*)p + 24);
    arc_store_weak(slot, 0);           /* weak-release the target */
}
static TypeInfo arc_weakref_ti = { "WeakReference", 32, arc_weakref_deinit, 0, 0, 0, 0 };

void* arc_weakref_new(void* target) {
    ObjHeader* h = (ObjHeader*)calloc(1, 32);
    h->strong = 1; h->weak = 1; h->type = &arc_weakref_ti;
    void** slot = (void**)((char*)h + 24);
    arc_store_weak(slot, target);      /* weak-retain target */
    g_alloc++;
    return h;
}
void* arc_weakref_try_get(void* wr) {
    if (!wr) return 0;
    void** slot = (void**)((char*)wr + 24);
    return arc_load_weak(slot);        /* +1 strong, or NULL if target died */
}
void arc_weakref_set(void* wr, void* target) {
    if (!wr) return;
    void** slot = (void**)((char*)wr + 24);
    arc_store_weak(slot, target);
}

/* ---- console --------------------------------------------------------- */
void arc_console_write(void* s, int nl) {
    if (s) fputs(arc_str_data(s), stdout);
    if (nl) fputc('\n', stdout);
}
void arc_console_write_int(long v, int nl) {
    printf("%ld", v);
    if (nl) putchar('\n');
}
void arc_console_write_bool(int v, int nl) {
    fputs(v ? "True" : "False", stdout);
    if (nl) putchar('\n');
}
void arc_console_newline(void) { putchar('\n'); }
