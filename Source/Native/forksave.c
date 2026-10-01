// Native half of ForkSave: fork() while holding the Boehm GC's allocation lock, using the
// GC's own fork hooks. Otherwise another thread may hold that lock at fork time, and the child,
// where that thread doesn't exist, deadlocks on its first allocation.
// This can't be done from C#: while this thread holds the lock, no managed code may run,
// not even the first call of a P/Invoke, which may allocate.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <unistd.h>

static void (*gc_atfork_prepare)(void);
static void (*gc_atfork_parent)(void);
static void (*gc_atfork_child)(void);
static void (*gc_disable)(void);
static int (*gc_collection_in_progress)(void);

static void *lookup(void *gc, const char *name)
{
    void *symbol = gc ? dlsym(gc, name) : NULL;
    return symbol ? symbol : dlsym(RTLD_DEFAULT, name);
}

// Returns 0 when the GC's fork hooks were found, -1 otherwise.
int forksave_init(void)
{
    // Unity loads the GC from libmonobdwgc-2.0.so; dlopen matches the already-loaded copy by soname.
    void *gc = dlopen("libmonoboehm-2.0.so.1", RTLD_LAZY | RTLD_NOLOAD);
    gc_atfork_prepare = lookup(gc, "GC_atfork_prepare");
    gc_atfork_parent = lookup(gc, "GC_atfork_parent");
    gc_atfork_child = lookup(gc, "GC_atfork_child");
    gc_disable = lookup(gc, "GC_disable");
    gc_collection_in_progress = lookup(gc, "GC_collection_in_progress");
    return gc_atfork_prepare && gc_atfork_parent && gc_atfork_child && gc_disable && gc_collection_in_progress ? 0 : -1;
}

// Whether an incremental collection is running; forksave_fork would first finish it synchronously.
// Read without the GC lock: a hint, not a guarantee.
int forksave_gc_in_progress(void)
{
    return gc_collection_in_progress();
}

// fork() semantics: the child's pid in the parent, 0 in the child, -1 with errno on failure.
// The GC must still be enabled here: GC_atfork_prepare finishes an in-progress incremental
// collection, which never progresses while the GC is disabled.
int forksave_fork(void)
{
    gc_atfork_prepare();
    pid_t pid = fork();
    if (pid == 0)
    {
        gc_atfork_child();
        // The child never collects: a stop-the-world would wait for threads that don't exist here.
        gc_disable();
        return 0;
    }
    int fork_errno = errno;
    gc_atfork_parent();
    errno = fork_errno;
    return pid;
}
