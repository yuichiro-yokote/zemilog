using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ZemiLog.Core;

namespace ZemiLog.App;

public sealed record AppSettings(string BaseUrl = "", string Model = "", int TimeoutSeconds = 90, string PlaybackMode = "Headphones", string? MicrophoneId = null);

public sealed class AppSettingsStore
{
    private const string CredentialTarget = "ZemiLog/QwenApiKey";
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemiLog", "settings.json");

    public async Task<(AppSettings Settings, string ApiKey)> LoadAsync()
    {
        AppSettings settings = new();
        if (File.Exists(_path))
        {
            await using var stream = File.OpenRead(_path);
            settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new();
        }
        return (settings, CredentialManager.Read(CredentialTarget) ?? string.Empty);
    }

    public async Task SaveAsync(AppSettings settings, string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(apiKey)) CredentialManager.Write(CredentialTarget, apiKey);
    }

    public QwenSettings ToQwenSettings(AppSettings settings, string apiKey) =>
        new(settings.BaseUrl, settings.Model, settings.TimeoutSeconds, apiKey);

    private static class CredentialManager
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        public static void Write(string target, string secret)
        {
            var bytes = Encoding.Unicode.GetBytes(secret);
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Type = 1,
                    TargetName = target,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = 2,
                    UserName = Environment.UserName
                };
                if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeCoTaskMem(blob); }
        }

        public static string? Read(string target)
        {
            if (!CredRead(target, 1, 0, out var pointer)) return null;
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                return credential.CredentialBlobSize == 0
                    ? string.Empty
                    : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            }
            finally { CredFree(pointer); }
        }
    }
}
