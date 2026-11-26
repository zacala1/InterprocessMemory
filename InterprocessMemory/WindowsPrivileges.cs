using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InterprocessMemory
{
    /// <summary>
    /// Reads the privileges of the token of this process on Windows.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static partial class WindowsPrivileges
    {
        /// <summary>The privilege that creating a kernel object in the <c>Global\</c> namespace needs.</summary>
        public const string CreateGlobal = "SeCreateGlobalPrivilege";

        private const uint TokenQuery = 0x0008;              // TOKEN_QUERY
        private const int TokenPrivilegesClass = 3;          // TOKEN_INFORMATION_CLASS.TokenPrivileges
        private const uint PrivilegeEnabled = 0x00000002;    // SE_PRIVILEGE_ENABLED
        private const int EntrySize = 12;                    // LUID_AND_ATTRIBUTES: an 8-byte LUID and the attributes

        /// <summary>
        /// True when the token of this process holds <paramref name="privilege"/> and has it enabled. False when it
        /// does not, and when the token cannot be read.
        /// </summary>
        public static bool IsEnabled(string privilege)
        {
            if (!LookupPrivilegeValue(null, privilege, out long wanted) ||
                !OpenProcessToken(GetCurrentProcess(), TokenQuery, out IntPtr token))
                return false;

            try
            {
                GetTokenInformation(token, TokenPrivilegesClass, null, 0, out int length);   // only asks for the size
                if (length < sizeof(int))
                    return false;

                var buffer = new byte[length];
                if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, length, out _))
                    return false;

                // TOKEN_PRIVILEGES: a count, then that many LUID_AND_ATTRIBUTES.
                int count = BitConverter.ToInt32(buffer, 0);
                for (int i = 0; i < count && sizeof(int) + EntrySize * (i + 1) <= length; i++)
                {
                    int offset = sizeof(int) + EntrySize * i;
                    if (BitConverter.ToInt64(buffer, offset) == wanted)
                        return (BitConverter.ToUInt32(buffer, offset + 8) & PrivilegeEnabled) != 0;
                }

                return false;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr GetCurrentProcess();

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool LookupPrivilegeValue(string? systemName, string name, out long luid);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            byte[]? tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
