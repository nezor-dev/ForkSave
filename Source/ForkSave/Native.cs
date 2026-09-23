using System;
using System.Runtime.InteropServices;

namespace ForkSave;

// "libc" is dllmapped to libc.so.6 by RimWorld's bundled Mono config.
internal static class Native
{
    public const int WNOHANG = 1;
    public const int SIGKILL = 9;

    private const int RTLD_NOW = 2;

    // Built from Source/Native/forksave.c with this soname. RimWorld loads mod assemblies from
    // bytes, so Mono can't find it next to ForkSave.dll; LoadForkHelper dlopens it by full path
    // first, and Mono's dlopen by name then matches that copy.
    private const string ForkHelper = "libforksave.so";

    [DllImport(ForkHelper, SetLastError = true)]
    public static extern int forksave_fork();

    [DllImport(ForkHelper)]
    private static extern int forksave_init();

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlopen(string path, int flags);

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlerror();

    [DllImport("libc", SetLastError = true)]
    public static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);

    // Exit without Unity/Mono shutdown or atexit handlers, which would touch state the child doesn't own.
    [DllImport("libc")]
    public static extern void _exit(int status);

    // Returns null on success, otherwise why the helper is unusable.
    public static string LoadForkHelper(string path)
    {
        if (dlopen(path, RTLD_NOW) == IntPtr.Zero)
        {
            return Marshal.PtrToStringAnsi(dlerror());
        }
        return forksave_init() == 0 ? null : "the GC's fork hooks (GC_atfork_*, GC_disable) were not found";
    }
}
