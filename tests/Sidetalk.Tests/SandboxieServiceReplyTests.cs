using System.Buffers.Binary;
using System.Text;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class SandboxieServiceReplyTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Sandboxie RPC: native WCHAR alignment and trailing padding", PathAlignment),
        ("Sandboxie RPC: user section stops at first NUL in fixed buffer", UserPadding),
        ("Sandboxie RPC: truncated and unterminated replies rejected", InvalidReplies)
    ];
    private static byte[] Packet(int length)
    {
        var data = Enumerable.Repeat((byte)0xcc, length).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 0);
        return data;
    }
    private static void PathAlignment()
    {
        const string path = @"C:\Sandboxie\Sandboxie.ini";
        var data = Packet(12 + (path.Length + 1) * 2);
        data[8] = 1;
        Encoding.Unicode.GetBytes(path + "\0").CopyTo(data, 10);
        var result = SandboxieServiceReply.Path(data);
        Assert.Equal(path, result.Path);
        Assert.True(result.IsHomePath);
    }
    private static void UserPadding()
    {
        var data = Packet(120);
        data[8] = 1; // service includes UAC-limited administrator membership, not elevation
        Encoding.Unicode.GetBytes("UserSettings_0123ABCD\0").CopyTo(data, 10);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(92, 4), 4);
        Encoding.Unicode.GetBytes("User\0").CopyTo(data, 96);
        var result = SandboxieServiceReply.User(data);
        Assert.Equal("UserSettings_0123ABCD", result.Section);
        Assert.Equal("User", result.Name);
    }
    private static void InvalidReplies()
    {
        Assert.Throws<InvalidDataException>(() => SandboxieServiceReply.Path(Packet(8)));
        Assert.Throws<InvalidDataException>(() => SandboxieServiceReply.Path(Packet(18)));
        var odd = Packet(19); odd[10] = 0; odd[11] = 0;
        Assert.Throws<InvalidDataException>(() => SandboxieServiceReply.Path(odd));
        Assert.Throws<InvalidDataException>(() => SandboxieServiceReply.User(Packet(96)));
        var data = Packet(120);
        Encoding.Unicode.GetBytes("UserSettings_0123ABCD\0").CopyTo(data, 10);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(92, 4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => SandboxieServiceReply.User(data));
    }
}