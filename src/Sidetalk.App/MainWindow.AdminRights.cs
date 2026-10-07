using System.Windows;
using Sidetalk.Core;

namespace Sidetalk.App;

public partial class MainWindow
{
    private bool _adminRightsAttentionShown;
    private string? _normalRecoveryScopeText;

    private static bool IsAdminRightsRepair(SetupRecoveryPlan? plan) => plan is { IsFirstSetup: false } &&
        plan.Changes.Any(change => !change.IsNoOp && change.Kind == SetupRecoveryChangeKind.RepairOwnedBoxSetting &&
            change.Key.Equals("DropAdminRights", StringComparison.OrdinalIgnoreCase));

    private static string SummarizeAdminRightsDetail(AppSnapshot snapshot, string detail) =>
        snapshot.AdminRights?.Condition is AdminRightsCondition.MissingOrEmpty or AdminRightsCondition.Disabled
            ? $"DropAdminRights 명시 값: {FormatAdminRightsValues(snapshot.AdminRights.ExplicitValues)}. 업데이트 중 권한 허용으로 바뀔 수 있습니다. 새 실행과 강제 종료는 차단됩니다."
            : detail;

    private void ApplyAdminRightsRecoveryPresentation(SetupRecoveryPlan plan)
    {
        var repair = IsAdminRightsRepair(plan);
        _normalRecoveryScopeText ??= RecoveryScopeText.Text;
        SetText(RecoveryScopeText, repair
            ? "이번 복구는 전용 박스의 관리자 권한 제한을 y로 되돌리는 변경에 새 승인이 필요합니다. 기존 자동 복구 동의를 사용하거나 앞으로의 자동 복구 동의를 추가하지 않습니다. 표시된 관리 창 정리 대상이 있으면 승인 후 닫히며 저장하지 않은 관리 설정은 사라질 수 있습니다."
            : _normalRecoveryScopeText);
        AutomaticRecoveryCheckBox.Visibility = repair ? Visibility.Collapsed : Visibility.Visible;
        PrepareRecoveryButton.Content = repair ? "관리자 권한 제한 복구" : "복구하고 실행 준비";
        if (repair)
        {
            _suppressRecoveryChoiceChange = true;
            try { AutomaticRecoveryCheckBox.IsChecked = false; }
            finally { _suppressRecoveryChoiceChange = false; }
            _automaticRecoveryChoiceDirty = false;
        }
    }

    private void ApplyAdminRightsPresentation(AppSnapshot snapshot)
    {
        var observation = snapshot.AdminRights;
        var changed = observation?.Condition is AdminRightsCondition.MissingOrEmpty or AdminRightsCondition.Disabled;
        SetText(AdminRightsSummaryText, observation?.Condition switch
        {
            AdminRightsCondition.Expected => "현재 관리자 권한 제한 설정은 확인됐습니다. 과거 업데이트 완료와 로그인 성공은 별도 확인이 필요합니다.",
            AdminRightsCondition.MissingOrEmpty => "보조 환경의 관리자 권한 제한 값이 없거나 비어 있습니다. 업데이트 중 Sandboxie에서 권한 허용을 선택하면 바뀔 수 있지만, 사용자가 어떤 항목을 승인했는지는 확인되지 않았습니다.",
            AdminRightsCondition.Disabled => "보조 환경의 관리자 권한 제한이 n으로 변경되어 있습니다. 업데이트 중 권한 허용을 선택하면 이 제한이 바뀔 수 있습니다. 현재 값과 과거 클릭은 구분합니다.",
            AdminRightsCondition.Unrecognized => "관리자 권한 제한 값이 현재 정책과 다릅니다. 중복·프로세스별·미검토 값은 이 제한 복구로 덮어쓰지 않습니다.",
            _ => "현재 관리자 권한 제한과 전용 프로세스 상태를 확인하지 못했습니다. 조회 차단 이유와 진단을 먼저 확인하세요."
        });
        SetText(AdminRightsValuesText, observation is null ? "DropAdminRights: 미확인" :
            $"DropAdminRights 명시 값: {FormatAdminRightsValues(observation.ExplicitValues)}\n상속 포함 값: {FormatAdminRightsValues(observation.EffectiveValues)}\n" +
            (observation.ProcessesQueried
                ? observation.ProcessIds.Count == 0 ? "전용 프로세스: 조회 시점에 없음" : $"전용 프로세스: {observation.ProcessIds.Count}개 · 시작 당시 적용 규칙과 실제 토큰은 미확인"
                : "전용 프로세스: 미확인" + (observation.ProcessFailure is null ? string.Empty : " · " + ReadableFailure(observation.ProcessFailure))));

        var action = changed ? observation!.ProcessesQueried
            ? observation.ProcessIds.Count > 0
                ? "보조 카카오톡 트레이 메뉴에서 정상 종료한 뒤 상태를 다시 확인하세요. 로그아웃할 필요는 없습니다. 실행 중 권한은 설정 복구로 소급 변경되지 않습니다."
                : "전용 환경에 프로세스가 없음을 확인했습니다. ‘초기 설정 · 진단’에서 변경 계획을 확인하고 관리자 권한 제한 복구를 새로 승인하세요."
            : "전용 프로세스 상태가 미확인입니다. 정상 종료와 빈 환경 확인 전에는 설정 복구를 진행할 수 없습니다."
            : string.Empty;
        SetText(AdminRightsActionText, action);
        AdminRightsActionText.Visibility = string.IsNullOrEmpty(action) ? Visibility.Collapsed : Visibility.Visible;
        if (changed && !_adminRightsAttentionShown)
        {
            KakaoUpdateExpander.IsExpanded = true;
            _adminRightsAttentionShown = true;
        }

        var images = new List<string>
        {
            $"호스트 카카오톡: {DisplayValue(snapshot.Kakao.Version)} · {(snapshot.Kakao.Available ? "배포자 서명 확인" : "설치·서명 확인 필요")}",
            "관찰한 버전만으로 최신 버전 또는 업데이트 완료 여부를 판정하지 않습니다."
        };
        if (observation?.Images.Count > 0)
        {
            foreach (var image in observation.Images)
            {
                images.Add($"전용 PID {image.ProcessId}의 조회 이미지: {image.QueryReportedImagePath ?? "경로 미확인"}");
                if (image.PhysicalResolution == ProgramPhysicalResolution.VerifiedBoxCopy && image.PhysicalFile is { } file)
                {
                    images.Add($"확인된 박스 프로그램 파일: {file.Path}\n버전 {DisplayValue(file.Version)} · {(file.PublisherVerified ? "배포자 서명 확인" : "서명 확인 실패")}");
                    if (image.VersionDiffersFromHost == true)
                        images.Add("호스트와 박스 프로그램 파일의 버전 차이를 확인했습니다. 계정 데이터와 분리된 안전한 사본 교체는 아직 지원하지 않습니다.");
                    else if (image.VersionDiffersFromHost == false)
                        images.Add("관찰한 파일 버전은 호스트와 같습니다. 업데이트 완료나 로그인 성공의 증거는 아닙니다.");
                    if (file.Failure is not null) images.Add(ReadableFailure(file.Failure));
                }
                else images.Add("실제 사용되는 물리 프로그램 사본은 미확인입니다. 호스트 경로 표시는 박스 사본이 없음을 증명하지 않습니다.");
                if (image.Failure is not null) images.Add(ReadableFailure(image.Failure));
            }
        }
        else images.Add("박스 내부 프로그램 사본과 실제 사용 파일은 미확인입니다. 프로그램 폴더나 계정 데이터를 탐색·삭제하지 않습니다.");
        SetText(KakaoUpdateEvidenceText, string.Join("\n", images));
    }

    private static string FormatAdminRightsValues(IReadOnlyList<string> values) => values.Count == 0
        ? "없음" : string.Join(" / ", values.Select(value => string.IsNullOrWhiteSpace(value) ? "빈 값" : SafeRecoveryText(value)));
}
