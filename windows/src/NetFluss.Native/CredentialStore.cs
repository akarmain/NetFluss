// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using System.Text;

namespace NetFluss.Native;

/// <summary>
/// Router credentials in Windows Credential Manager — the counterpart of the macOS
/// keychain items (<c>com.local.netfluss.unifi</c> and friends). Generic credentials with
/// local-machine persistence: tied to this user on this PC, never roamed, and encrypted
/// by Windows with the user's logon secret, so nothing sensitive lands in settings.json.
/// </summary>
public static unsafe class CredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    /// <summary>"NetFluss:unifi:192.168.1.1" — visible, and removable, in Credential Manager.</summary>
    public static string Target(string service, string account) => $"NetFluss:{service}:{account.Trim().ToLowerInvariant()}";

    /// <summary>Stores a pair (user and password, or key and secret) under one target.</summary>
    public static bool Save(string service, string account, string first, string second)
        => Write(Target(service, account), first, second);

    public static (string First, string Second)? Load(string service, string account)
    {
        if (!CredReadW(Target(service, account), CredTypeGeneric, 0, out var credential))
        {
            return null;
        }

        try
        {
            var cred = (Credential*)credential;
            if (cred->CredentialBlobSize == 0 || cred->CredentialBlob == null)
            {
                return null;
            }

            var secret = new string((char*)cred->CredentialBlob, 0, (int)cred->CredentialBlobSize / sizeof(char));
            var user = cred->UserName == null ? string.Empty : new string(cred->UserName);
            return (user, secret);
        }
        finally
        {
            CredFree(credential);
        }
    }

    public static bool Exists(string service, string account) => Load(service, account) is not null;

    public static void Delete(string service, string account)
    {
        // A missing credential is fine: the goal is that none exists afterwards.
        _ = CredDeleteW(Target(service, account), CredTypeGeneric, 0);
    }

    private static bool Write(string target, string user, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        fixed (char* targetPtr = target)
        fixed (char* userPtr = user)
        fixed (byte* blobPtr = blob)
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CredPersistLocalMachine,
            };

            var written = CredWriteW(ref credential, 0);
            Array.Clear(blob);
            return written;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public int Type;
        public char* TargetName;
        public char* Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, uint flags, out nint credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
