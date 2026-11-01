using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace iSay.Updates;

public static class SafeArchive
{
    public static async Task ExtractAsync(UpdateComponent component, string archivePath, string destination,
        UpdateConfiguration configuration, CancellationToken cancellationToken = default)
    {
        // 哈希与展开共用同一只读句柄，避免校验后重新打开被替换的包。
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await UpdateSafety.VerifyStreamAsync(input, component.Size, component.Sha256, cancellationToken);
        var root = UpdateSafety.PrepareDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidDataException("更新暂存目录必须为空。");
        var expected = component.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in expected.Keys)
        {
            UpdateSafety.ValidateApplicationPath(path);
            var parent = path;
            while (parent.LastIndexOf('/') is var separator && separator >= 0)
            { parent = parent[..separator]; directories.Add(parent); }
        }
        if (expected.Keys.Any(directories.Contains)) throw new InvalidDataException("更新文件与目录冲突。");
        ValidateDirectoryRecord(input, checked(configuration.MaximumFiles * 2 + 1));
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        // 先验证整个中央目录，再创建任何展开文件。
        foreach (var entry in archive.Entries)
        {
            var directory = entry.FullName.EndsWith('/');
            var path = directory ? entry.FullName[..^1] : entry.FullName;
            UpdateSafety.ValidateApplicationPath(path);
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (mode is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                (mode == 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0) && !directory)
                throw new InvalidDataException("更新包不能包含链接或特殊文件。");
            if (!seen.Add(path)) throw new InvalidDataException("更新包存在重复路径。");
            if (directory)
            {
                if (!directories.Contains(path) || entry.Length != 0) throw new InvalidDataException("更新包包含未知目录。");
                continue;
            }
            if (!expected.TryGetValue(path, out var file) || file.Size != entry.Length || entry.CompressedLength > component.Size)
                throw new InvalidDataException("更新包文件与签名清单不一致。");
            files.Add(path);
            expanded = checked(expanded + file.Size);
            if (files.Count > configuration.MaximumFiles || expanded > component.MaximumExpandedBytes || expanded > configuration.MaximumExpandedBytes)
                throw new InvalidDataException("更新包展开超出限额。");
        }
        if (files.Count != expected.Count) throw new InvalidDataException("更新包缺少签名文件。");
        foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = expected[entry.FullName];
            var target = UpdateSafety.ResolveInside(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var compressed = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long count = 0;
            while (true)
            {
                var read = await compressed.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                count = checked(count + read);
                if (count > file.Size) throw new InvalidDataException("更新文件展开超过签名长度。");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (count != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新文件展开校验失败。");
            await output.FlushAsync(cancellationToken);
        }
    }

    private static void ValidateDirectoryRecord(FileStream stream, int maximumEntries)
    {
        // 当前应用包不需要 ZIP64；先检查条目数，避免恶意中央目录造成大量内存分配。
        var buffer = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - buffer.Length;
        stream.ReadExactly(buffer);
        for (var offset = buffer.Length - 22; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset)) != 0x06054b50) continue;
            var record = buffer.AsSpan(offset);
            if (offset + 22 + BinaryPrimitives.ReadUInt16LittleEndian(record[20..]) != buffer.Length) continue;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(record[6..]) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) != count || count == ushort.MaxValue || count > maximumEntries ||
                length == uint.MaxValue || start == uint.MaxValue || (long)start + length != stream.Length - buffer.Length + offset)
                throw new InvalidDataException("更新 ZIP 中央目录无效或超出限额。");
            ValidateCentralHeaders(stream, start, length, count);
            return;
        }
        throw new InvalidDataException("更新 ZIP 结束记录无效。");
    }

    private static void ValidateCentralHeaders(FileStream stream, uint start, uint length, ushort count)
    {
        var end = (long)start + length;
        stream.Position = start;
        Span<byte> header = stackalloc byte[46];
        for (var index = 0; index < count; index++)
        {
            if (stream.Position + header.Length > end) throw new InvalidDataException("更新 ZIP 中央目录条目不足。");
            stream.ReadExactly(header);
            var name = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var comment = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50 || name is 0 or > 880 || extra > 1024 || comment > 1024 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) == uint.MaxValue || BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) == uint.MaxValue ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[42..]) >= start || stream.Position + name + extra + comment > end)
                throw new InvalidDataException("更新 ZIP 中央目录元数据无效或超出限额。");
            stream.Position += name + extra + comment;
        }
        if (stream.Position != end) throw new InvalidDataException("更新 ZIP 中央目录实际条目数与声明不符。");
    }
}
