// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using NetFluss.Core;

// Usage:
//   dotnet run --project windows/tools/UpdateSigning -- sign <SHA256SUMS.txt>
//       Signs with the PEM private key in NETFLUSS_UPDATE_SIGNING_KEY, writes
//       <file>.sig beside it, and checks the result against the public key compiled into
//       the app — so a release signed with the wrong key fails here, not on users' PCs.
//   dotnet run --project windows/tools/UpdateSigning -- verify <SHA256SUMS.txt>
//       Checks <file>.sig against the compiled-in public key.
//   dotnet run --project windows/tools/UpdateSigning -- keygen <private-key-file>
//       Writes a new private key to the file and prints the public key for
//       UpdateSignature.PublicKey. Store the private key as the GitHub secret
//       NETFLUSS_UPDATE_SIGNING_KEY and keep a copy somewhere safe.

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: UpdateSigning sign|verify <SHA256SUMS.txt> | keygen <private-key-file>");
    return 2;
}

var (verb, path) = (args[0], args[1]);
switch (verb)
{
    case "keygen":
    {
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"error: {path} exists; refusing to overwrite a key.");
            return 1;
        }

        var (privateKey, publicKey) = UpdateSignature.CreateKeyPair();
        File.WriteAllText(path, privateKey);
        Console.WriteLine(publicKey);
        return 0;
    }

    case "sign":
    {
        var key = Environment.GetEnvironmentVariable("NETFLUSS_UPDATE_SIGNING_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("error: NETFLUSS_UPDATE_SIGNING_KEY is not set.");
            return 1;
        }

        var data = File.ReadAllBytes(path);
        var signature = UpdateSignature.Sign(data, key);
        if (!UpdateSignature.Verify(data, signature))
        {
            Console.Error.WriteLine("error: the signing key does not match UpdateSignature.PublicKey; the app would refuse this release.");
            return 1;
        }

        File.WriteAllText(path + ".sig", signature);
        Console.WriteLine($"signed {Path.GetFileName(path)}");
        return 0;
    }

    case "verify":
    {
        var ok = UpdateSignature.Verify(File.ReadAllBytes(path), File.Exists(path + ".sig") ? File.ReadAllText(path + ".sig") : null);
        Console.WriteLine(ok ? "signature OK" : "signature INVALID");
        return ok ? 0 : 1;
    }

    default:
        Console.Error.WriteLine($"error: unknown command '{verb}'.");
        return 2;
}
