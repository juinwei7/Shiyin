#:project ../../windows/src/Shiyin.Services/Shiyin.Services.csproj
// 更新金鑰（design-windows §14.2、D50）：ECDSA P-256
//   dotnet run scripts/windows/update-key.cs -- keygen <私鑰.pem> <公鑰.pem>
//       建一次。公鑰貼進 windows/src/Shiyin.App/AppHost.cs 的 UpdateKey.Public；私鑰放 GitHub secrets（SHIYIN_UPDATE_KEY，要使用者同意）
//   dotnet run scripts/windows/update-key.cs -- manifest <版本> <x64|arm64> <輸出.json> <套件檔…>
//       寫 shiyin-update-win-<架構>.json：版本、架構、每個套件檔的大小和 SHA-256
//   dotnet run scripts/windows/update-key.cs -- sign <manifest.json>
//       用環境變數 SHIYIN_UPDATE_KEY（私鑰的 PEM）簽，寫 <manifest.json>.sig
//   dotnet run scripts/windows/update-key.cs -- verify <manifest.json> <公鑰.pem>
using Shiyin.Services;

switch (args.FirstOrDefault())
{
    case "keygen" when args.Length == 3:
        if (File.Exists(args[1])) { Console.Error.WriteLine($"{args[1]} 已經存在，不覆蓋"); return 1; }
        var (privateKey, publicKey) = UpdateSignature.Generate();
        File.WriteAllText(args[1], privateKey);
        File.WriteAllText(args[2], publicKey);
        Console.WriteLine(publicKey);
        return 0;
    case "manifest" when args.Length >= 5:
        var files = new List<UpdateManifest.PackageFile>();
        foreach (var path in args[4..])
            files.Add(new UpdateManifest.PackageFile(Path.GetFileName(path), new FileInfo(path).Length, await ModelStore.Sha256(path)));
        File.WriteAllText(args[3], new UpdateManifest(args[1], args[2], files).Json());
        Console.WriteLine(File.ReadAllText(args[3]));
        return 0;
    case "sign" when args.Length == 2:
        var key = Environment.GetEnvironmentVariable("SHIYIN_UPDATE_KEY");
        if (string.IsNullOrWhiteSpace(key)) { Console.Error.WriteLine("沒有 SHIYIN_UPDATE_KEY"); return 1; }
        File.WriteAllText(args[1] + ".sig", UpdateSignature.Sign(File.ReadAllBytes(args[1]), key));
        return 0;
    case "verify" when args.Length == 3:
        var ok = UpdateSignature.Verify(File.ReadAllBytes(args[1]), File.ReadAllText(args[1] + ".sig"), File.ReadAllText(args[2]));
        Console.WriteLine(ok ? "OK" : "簽章不符");
        return ok ? 0 : 1;
    default:
        Console.Error.WriteLine("用法：update-key.cs keygen|manifest|sign|verify …");
        return 2;
}
