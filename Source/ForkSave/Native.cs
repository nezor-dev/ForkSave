using System.Runtime.InteropServices;

namespace ForkSave;

// "libc" is dllmapped to libc.so.6 by RimWorld's bundled Mono config.
internal static class Native
{
    public const int WNOHANG = 1;
    public const int SIGKILL = 9;

    [DllImport("libc", SetLastError = true)]
    public static extern int fork();

    [DllImport("libc", SetLastError = true)]
    public static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);

    // Exit without Unity/Mono shutdown or atexit handlers, which would touch state the child doesn't own.
    [DllImport("libc")]
    public static extern void _exit(int status);
}
