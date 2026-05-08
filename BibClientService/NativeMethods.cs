using System.Runtime.InteropServices;

namespace BibClientService
{
    // P/Invoke для запуска процесса с UI в пользовательском сеансе из контекста SYSTEM.
    // Сервисы Windows работают в Session 0 (без UI). Чтобы показать окно пользователю,
    // нужно получить его токен сеанса и запустить процесс через CreateProcessAsUser.
    internal static class NativeMethods
    {
        public const uint TOKEN_ALL_ACCESS = 0xF01FF;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize;
            public uint dwXCountChars, dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        public enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
        public enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

        [DllImport("kernel32.dll")]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(
            IntPtr hExistingToken, uint dwDesiredAccess,
            ref SECURITY_ATTRIBUTES lpTokenAttributes,
            SECURITY_IMPERSONATION_LEVEL impersonationLevel,
            TOKEN_TYPE tokenType, out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(
            IntPtr hToken, string lpApplicationName, string? lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
