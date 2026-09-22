using System.Buffers.Binary;
using System.Text;

namespace Sidetalk.Core;

internal static class SandboxieServiceReply
{
    public static SandboxieIniPath Path(byte[] reply)
    {
        RequireLength(reply, 12);
        var path = NullTerminated(reply, 10, (reply.Length - 10) / 2);
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new InvalidDataException("Sandboxie 설정 경로가 절대 경로가 아닙니다.");
        return new(System.IO.Path.GetFullPath(path), reply[8] != 0);
    }

    public static SandboxieUser User(byte[] reply)
    {
        RequireLength(reply, 98);
        var section = NullTerminated(reply, 10, 40);
        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(92, 4));
        if (nameLength < 0 || nameLength > 32768 || 96L + ((long)nameLength + 1) * 2 > reply.Length)
            throw new InvalidDataException("Sandboxie 사용자 이름 길이가 유효하지 않습니다.");
        var name = NullTerminated(reply, 96, nameLength + 1);
        if (name.Length != nameLength || !section.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sandboxie 사용자 섹션 응답이 유효하지 않습니다.");
        return new(section, name, reply[8] != 0);
    }

    private static void RequireLength(byte[] reply, int minimum)
    {
        if (reply.Length < minimum || reply.Length % 2 != 0 || BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(0, 4)) != reply.Length ||
            BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4, 4)) != 0)
            throw new InvalidDataException("Sandboxie 서비스 응답이 유효하지 않습니다.");
    }

    private static string NullTerminated(byte[] reply, int offset, int capacity)
    {
        if (capacity <= 0 || offset < 0 || (long)offset + capacity * 2L > reply.Length)
            throw new InvalidDataException("Sandboxie 응답 문자열 범위가 유효하지 않습니다.");
        for (var i = 0; i < capacity; i++)
            if (reply[offset + i * 2] == 0 && reply[offset + i * 2 + 1] == 0)
                return new UnicodeEncoding(false, false, true).GetString(reply, offset, i * 2);
        throw new InvalidDataException("Sandboxie 응답 문자열이 종료되지 않았습니다.");
    }
}