using System.Buffers.Binary;
using System.Text;
using Shiyin.Localization;

namespace Shiyin.Services.Capture;

/// <summary>修復沒有正常結束的 WAV：用實際檔案大小改寫 RIFF 與 data 的大小（M§8，同 Mac 的 WAVRepair）</summary>
public static class WavRepair
{
    public sealed class RepairException(string message) : Exception(message);

    public static void Repair(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var size = file.Length;
        if (size <= 44) throw new RepairException(L.T("錄音檔太小，沒有內容"));
        Span<byte> header = stackalloc byte[12];
        file.ReadExactly(header);
        if (Encoding.ASCII.GetString(header[..4]) != "RIFF" || Encoding.ASCII.GetString(header[8..]) != "WAVE")
            throw new RepairException(L.T("不是 WAV 檔"));
        long offset = 12;
        Span<byte> chunk = stackalloc byte[8];
        while (offset + 8 <= size)
        {
            file.Position = offset;
            if (file.Read(chunk) < 8) break;
            var id = Encoding.ASCII.GetString(chunk[..4]);
            long chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (id == "data")
            {
                Write(file, offset + 4, (uint)Math.Min(size - offset - 8, uint.MaxValue));
                Write(file, 4, (uint)Math.Min(size - 8, uint.MaxValue));
                return;
            }
            offset += 8 + chunkSize + chunkSize % 2;
        }
        throw new RepairException(L.T("找不到音訊資料區段"));
    }

    static void Write(FileStream file, long offset, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        file.Position = offset;
        file.Write(bytes);
    }
}
