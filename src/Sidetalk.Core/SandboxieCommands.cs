using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Sidetalk.Core;

public static class SandboxieCommands
{
    public static ProcessStartInfo Launch(string startExe, string boxName, string kakaoExe)
    {
        var command = Base(startExe, boxName);
        if (!Path.IsPathFullyQualified(kakaoExe) || !Path.GetFileName(kakaoExe).Equals("KakaoTalk.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("공식 카카오톡의 절대 경로가 필요합니다.");
        command.ArgumentList.Add(kakaoExe);
        command.WorkingDirectory = Path.GetDirectoryName(kakaoExe)!;
        return command;
    }
    public static ProcessStartInfo Stop(string startExe, string boxName)
    {
        var command = Base(startExe, boxName);
        command.ArgumentList.Add("/terminate");
        return command;
    }
    private static ProcessStartInfo Base(string startExe, string boxName)
    {
        if (!Regex.IsMatch(boxName, @"\ASideTalk_[a-f0-9]{23}\z")) throw new ArgumentException("SIDETALK 전용 환경만 제어할 수 있습니다.");
        if (!Path.IsPathFullyQualified(startExe) || !Path.GetFileName(startExe).Equals("Start.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Sandboxie Start.exe의 절대 경로가 필요합니다.");
        var command = new ProcessStartInfo(startExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(startExe)! };
        command.ArgumentList.Add("/silent");
        command.ArgumentList.Add("/box:" + boxName);
        return command;
    }
}
