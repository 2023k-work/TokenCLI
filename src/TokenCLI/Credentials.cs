using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TokenCLI;

public interface ICredentials
{
    string? Read(string provider);
    bool Exists(string provider);
    void Save(string provider, string token);
    void Delete(string provider);
}

public sealed class CredentialException : Exception
{
    public CredentialException() : base("credential_error") { }
}

public sealed class WindowsCredentials : ICredentials
{
    // Separate namespace: do not read or migrate old TokenBridge credentials implicitly.
    private readonly string prefix;
    public WindowsCredentials(string prefix = "TokenCLI") => this.prefix = prefix;
    public static void ValidateProvider(string provider)
    {
        if (provider is not ("openai" or "anthropic")) throw new ArgumentException("invalid_provider");
    }
    private string Target(string provider)
    {
        ValidateProvider(provider);
        if (!OperatingSystem.IsWindows()) throw new CredentialException();
        return prefix + "/" + provider;
    }
    private IntPtr Open(string provider)
    {
        if (CredRead(Target(provider), 1, 0, out var pointer)) return pointer;
        if (Marshal.GetLastWin32Error() == 1168) return IntPtr.Zero;
        throw new CredentialException();
    }
    public bool Exists(string provider)
    {
        var pointer = Open(provider);
        if (pointer == IntPtr.Zero) return false;
        CredFree(pointer);
        return true;
    }
    public string? Read(string provider)
    {
        var pointer = Open(provider);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var value = Marshal.PtrToStructure<Credential>(pointer);
            if (value.BlobSize == 0 || value.BlobSize > 2560 || value.BlobSize % 2 != 0) throw new CredentialException();
            return Marshal.PtrToStringUni(value.Blob, checked((int)value.BlobSize / 2));
        }
        finally { CredFree(pointer); }
    }
    public void Save(string provider, string token)
    {
        var target = Target(provider);
        if (token.Length == 0 || token.Any(c => c < 33 || c > 126) || Encoding.Unicode.GetByteCount(token) > 2560)
            throw new ArgumentException("invalid_token");
        var bytes = Encoding.Unicode.GetBytes(token);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var value = new Credential
            {
                Type = 1, TargetName = target, BlobSize = (uint)bytes.Length,
                Blob = buffer, Persist = 2, UserName = provider
            };
            if (!CredWrite(ref value, 0)) throw new CredentialException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.FreeHGlobal(buffer);
        }
    }
    public void Delete(string provider)
    {
        if (!CredDelete(Target(provider), 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new CredentialException();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr pointer);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr pointer);
}
