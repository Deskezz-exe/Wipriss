using System.Runtime.InteropServices;

namespace Wipriss.ProcessTools;

public enum SignatureState
{
    /// <summary>Validly signed, either inside the file or through a Windows catalog.</summary>
    Signed,
    Unsigned,
    /// <summary>Has a signature that doesn't match: the file was changed after signing, or the signature is bad.</summary>
    Broken,
    /// <summary>Couldn't check (file gone or locked).</summary>
    Unknown,
}

public sealed record FileSignature(SignatureState State, string? Publisher);

/// <summary>
/// Checks a program's digital signature the way Windows does (WinVerifyTrust), fully offline:
/// no revocation lookups, so checking never sends anything over the network.
/// </summary>
public static class FileTrust
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);

    public static FileSignature Check(string path)
    {
        if (!File.Exists(path)) return new FileSignature(SignatureState.Unknown, null);

        int result = VerifyEmbedded(path, out string? publisher);
        if (result == 0) return new FileSignature(SignatureState.Signed, publisher);
        if (result is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
            return VerifyCatalog(path) ? new FileSignature(SignatureState.Signed, "Microsoft Windows") : new FileSignature(SignatureState.Unsigned, null);
        return new FileSignature(SignatureState.Broken, null);
    }

    private static int VerifyEmbedded(string path, out string? publisher)
    {
        var file = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(path),
        };
        IntPtr filePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            return Verify(WTD_CHOICE_FILE, filePtr, out publisher);
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePtr);
            Marshal.FreeCoTaskMem(file.pcwszFilePath);
        }
    }

    /// <summary>Windows' own files are often signed through a catalog (.cat) instead of inside the file.</summary>
    private static bool VerifyCatalog(string path)
    {
        foreach (string algorithm in new[] { "SHA256", "SHA1" })
        {
            if (!CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
            try
            {
                byte[] hash;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    uint size = 0;
                    CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle.DangerousGetHandle(), ref size, null, 0);
                    if (size == 0) continue;
                    hash = new byte[size];
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle.DangerousGetHandle(), ref size, hash, 0)) continue;
                }

                IntPtr catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, IntPtr.Zero);
                if (catalog == IntPtr.Zero) continue;
                try
                {
                    var info = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>() };
                    if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0)) continue;
                    if (VerifyInCatalog(path, info.wszCatalogFile, hash, admin) == 0) return true;
                }
                finally
                {
                    CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                CryptCATAdminReleaseContext(admin, 0);
            }
        }

        return false;
    }

    private static int VerifyInCatalog(string path, string catalogFile, byte[] hash, IntPtr admin)
    {
        var info = new WintrustCatalogInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustCatalogInfo>(),
            pcwszCatalogFilePath = Marshal.StringToCoTaskMemUni(catalogFile),
            pcwszMemberTag = Marshal.StringToCoTaskMemUni(Convert.ToHexString(hash)),
            pcwszMemberFilePath = Marshal.StringToCoTaskMemUni(path),
            pbCalculatedFileHash = Marshal.AllocCoTaskMem(hash.Length),
            cbCalculatedFileHash = (uint)hash.Length,
            hCatAdmin = admin,
        };
        Marshal.Copy(hash, 0, info.pbCalculatedFileHash, hash.Length);
        IntPtr infoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustCatalogInfo>());
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            return Verify(WTD_CHOICE_CATALOG, infoPtr, out _);
        }
        finally
        {
            Marshal.FreeCoTaskMem(infoPtr);
            Marshal.FreeCoTaskMem(info.pcwszCatalogFilePath);
            Marshal.FreeCoTaskMem(info.pcwszMemberTag);
            Marshal.FreeCoTaskMem(info.pcwszMemberFilePath);
            Marshal.FreeCoTaskMem(info.pbCalculatedFileHash);
        }
    }

    private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    private static int Verify(uint choice, IntPtr union, out string? publisher)
    {
        var data = new WintrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = choice,
            pUnion = union,
            dwStateAction = WTD_STATEACTION_VERIFY,
            // Offline: never fetch revocation lists or certificates from the internet.
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        Guid action = GenericVerifyV2;
        int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data); // INVALID_HANDLE_VALUE: no UI at all
        publisher = result == 0 ? SignerName(data.hWVTStateData) : null;
        data.dwStateAction = WTD_STATEACTION_CLOSE;
        WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        return result;
    }

    /// <summary>The signer's name from the verified signature ("Mozilla Corporation").</summary>
    private static string? SignerName(IntPtr state)
    {
        IntPtr provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero) return null;
        IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero) return null;

        // CRYPT_PROVIDER_SGNR: cbStruct, FILETIME, csCertChain, then pasCertChain (offset 16 on 32 and 64 bit)
        if (Marshal.ReadInt32(signer, 12) == 0) return null;
        IntPtr chain = Marshal.ReadIntPtr(signer, 16);
        // CRYPT_PROVIDER_CERT: cbStruct, then pCert (pointer-aligned)
        IntPtr certificate = chain == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(chain, IntPtr.Size);
        if (certificate == IntPtr.Zero) return null;

        // The organization ("Microsoft Corporation") says more than the certificate name (".NET").
        IntPtr organizationOid = Marshal.StringToHGlobalAnsi("2.5.4.10");
        try
        {
            return NameOf(certificate, CERT_NAME_ATTR_TYPE, organizationOid) ?? NameOf(certificate, CERT_NAME_SIMPLE_DISPLAY_TYPE, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(organizationOid);
        }
    }

    private static string? NameOf(IntPtr certificate, uint type, IntPtr typePara)
    {
        var name = new char[256];
        int length = CertGetNameStringW(certificate, type, 0, typePara, name, name.Length);
        string text = length > 1 ? new string(name, 0, length - 1).Trim() : "";
        return text.Length == 0 ? null : text;
    }

    private const uint CERT_NAME_ATTR_TYPE = 3, CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll")]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signerIndex, bool counterSigner, uint counterSignerIndex);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode)]
    private static extern int CertGetNameStringW(IntPtr certificate, uint type, uint flags, IntPtr typePara, char[] name, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustCatalogInfo
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll")]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint hashSize, uint flags, IntPtr previous);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
