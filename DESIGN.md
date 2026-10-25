# SIDETALK design

## Direction and platform

SIDETALK is a Windows 11 WPF utility for operating a second official KakaoTalk instance in a persistent owned Sandboxie environment. The September 16 redesign follows the user's request for a cooler, more playful interface: a compact transport console, charcoal surfaces, an acid-lime launch control, and a paired conversation mark. This is a flat native interface, not a simulated physical device.

The native title bar, resize/minimize/close controls, standard dialogs and keyboard behavior remain. DWM colors only SIDETALK's own caption; it does not touch other applications. No decorative animation, invented activity, login status or safety score.

Windows High Contrast overrides the normal color tokens with the user's Window/WindowText, Highlight/HighlightText, GrayText and ControlText palette. Existing main and quick-launch windows, programmatically assigned status colors, primary/disabled actions, tooltips and custom scrollbar ink update on the WPF dispatcher when Windows preferences change. Turning contrast off restores the normal colors below. Native caption colors return to the OS default while contrast is enabled. These are application resource changes only; Windows settings and execution/recovery authority are untouched.

## Composition

The wordmark and short Korean introduction lead into three native horizontal tabs: 보조 카카오톡, 초기 설정 · 진단 and 실행 · 식별. The October 7 refinement follows the approved third concept: a restrained lime launch control, one bordered execution card, and two dependency rows inside one divided component card. A neutral, static 자동 확인 켜짐 indicator describes the enabled inspection cadence, without implying verified isolation or login. The main card reserves space for actual operation feedback and keeps manual refresh secondary. First-setup guidance appears only for missing dependencies or an unprepared environment. Recovery and interrupted evidence remain visible in diagnostics; 환경 상세 · 진단 and 설치 · 수동 설정 fold the less frequent technical details without removing checks or exports. The third tab separates profile-scoped quick-launch consent, per-user shortcuts/startup and secondary-window appearance choices. The footer reads the actual product version from SidetalkBuild.

The default window is 880 × 740 DIPs; minimum is 740 × 640. Content scrolls vertically at smaller sizes while shell, navigation and footer remain outside the scrolling content. Long paths, warning messages and Korean labels wrap. Inter-tab spacing uses padding to preserve complete labels in WPF TabPanel layout.

## Tokens

| Role | Value |
|---|---|
| Canvas | #141619 |
| Surface | #202328 |
| Hover surface | #2C3036 |
| Primary text | #F4F5F0 |
| Secondary text | #B1B6BE |
| Action / observed activity | #D5FA61 |
| Action hover | #E3FF91 |
| Divider | #3A3F47 |
| Policy limitation | #F2CE8B |
| Blocked / error | #FFADA3 |
| Disabled text / surface | #9299A3 / #292D33 |

Use Segoe UI with Malgun Gothic fallback. Body is 14 DIPs, regular weight, 23 DIP line height. Status titles are 29 DIPs semibold; section headings 18; supporting captions 12. Surface radius is 16 DIPs and button radius 8. Native window chrome is retained. Scrollbars use the native ScrollBar/Track/Thumb controls with dark paint and normal drag, paging and wheel behavior.

## State and action language

- Ready preserves the controller's title and detail, including an unsuccessful launch message when the environment is empty again.
- Running displays 보조 환경 실행 중 and an 실행 중 badge. This means processes were observed, not that login or messaging succeeded.
- The visible amber explanation distinguishes current configuration verification from the unverified rules applied at process start. Full policy wording remains in diagnostics. Only the exact standard explanation is condensed; lifecycle feedback and unfamiliar detail remain on the primary screen.
- Blocked, setup-required and unknown states retain the controller's explanation. Application errors clear stale availability and disable actions.
- Normal exit is KakaoTalk's own tray command. 보조 카카오톡 강제 종료 clearly names SIDETALK's owned-environment-wide force-stop, which retains its confirmation dialog.
- Launch remains the lime primary action; force-stop remains secondary. During a normal read inspection, the last successful displayed ActionsPermitted and CanLaunch/CanStop values only determine whether one explicit user request can be accepted. They never authorize execution. Initial, failed, restore-required and invalidated observations keep requests disabled.
- An accepted request immediately shows progress and a cancel control, reserves a single intent before any await or confirmation dialog, and disables competing requests. It waits for the active inspection, retains the existing confirmation, then performs a fresh inspection before dispatch. Installation, ownership, profile and policy revalidation, the controller gate and CommandJournal lock remain in Core. Recovery and dispatched commands continue to exclude competing actions.
- Cancel, minimize and closing invalidate a pending intent; an old click is never replayed after a delayed response. Closing is deferred once a command or recovery operation has been dispatched. Inspection generations prevent a response from before closing, restoration or a newer operation from replacing current state. Optional recurrence prevention and repair availability do not gate a verified launch; unresolved actual setup changes and command identity still do.

## Interaction and verification

Inspection runs on opening and every four seconds. A normal scheduled read retains the displayed observation, main request buttons, refresh button, focus and scroll position. It adds no progress text or periodic live announcement. The precise last successful snapshot time updates only after a successful current response, inside the indicator's hover/keyboard-focus tooltip and the diagnostic details. Recent execution, recovery and interrupted-evidence failures remain separate from that success time. An unobserved environment starts with 환경 확인 중 and disabled execution requests.

Manual refresh joins an existing inspection task and reserves one manual request. Only its fixed-width button changes to 확인 중… after 300 ms; cancellation on completion, failure, minimize or close prevents delayed feedback from appearing afterward. Scheduled reads never cancel pending actions or replace their progress text. Actual launch, force-stop, recovery and preservation progress/results remain visible in the main card and their existing diagnostic areas. The complete read observation is published together, so a partial successful read does not briefly clear a still-unverified recovery failure. Identical check/plan lists retain their existing item containers. A previous observation remains labelled as historical after a failed inspection and cannot earn action permission. Buttons expose hover, pressed, keyboard-focus and disabled states; state text is meaningful without color.

Minimizing keeps the four-second Core inspection cadence for pending command reconciliation and consented automatic recovery. Successful snapshot, recovery-plan and evidence responses defer their visual updates while minimized; failure and recovery-progress messages still update. Restoration invalidates earlier responses and requires a new inspection before accepting requests. This reduces hidden visual updates; it does not claim fewer Core calls or measured improvement in real-device CPU, memory or responsiveness.

Interaction checks use a delayed fake controller and explicit TaskCompletionSource barriers for clicks during inspection, failed reads, duplicate intent, cancellation, competing recovery, late responses, closing and minimize/restore. They exercise native WPF controls and production handlers with synthetic data. Their outcome and measured changes are recorded in [VERIFICATION.md](docs/VERIFICATION.md), separately from real Windows 11 + Sandboxie and account tests.

The native WPF client area was rendered at default and minimum dimensions. Separate fixture snapshots cover ready, running, blocked, diagnostics, error, launch-failed and stop-residual states; they are explicitly synthetic and cannot prove real account behavior. A distinct read-only render uses the real user environment. Final visual review found the tab labels, dark scrolling controls, complete shell and preserved action feedback ready to ship.

## Approved preparation

The existing diagnostics tab now starts with a preparation panel when setup or recovery is needed. The first approval-required plan opens this tab once. It names the actual global selectors to remove, the current-user recurrence option, verified management-window termination targets, preserved data and blocking reasons. Automatic recurrence repair is opt-in and revocable. Semantic plan changes reset an unsaved consent choice.

Preparation shows backup, prevention, repair and reinspection progress. Competing actions are disabled; closing is deferred during a write. Mutation failures clear stale launch readiness. A repair-only inspection failure retains execution permission only from the current successful execution inspection. Inspection, diagnose and render paths never apply recovery. The recovery panel uses existing colors, typography and scroll behavior; no account or login success is inferred.

The October 13 approval refinement keeps this panel and its approval actions. Plain-language change purposes, a short preservation summary, unsaved management-setting loss warning and the user's next action precede approval. Exact selectors, current-user option, management-window targets and automatic scope remain in a collapsed technical expander; full preserved-data details and history access remain available. A missing current management target is distinguished from a future consented automatic-repair target. Preparation, settings application, reinspection, evidence preservation and revoking automatic consent use separate presentation labels. An unsuccessful write may have applied some settings: current settings and backup preservation never imply a rollback. Engine result details, including verified repair with unsuccessful optional consent persistence, remain visible.

Interrupted evidence has its own read-only classification and explicitly approved preservation action, independent of whether a settings plan can apply. Original bytes, hashes, locations and operation relationships are retained. Preserving evidence is not proof of completion. Unknown evidence remains a blocker and the same approved first-setup candidate is reused after fresh validation.

## Quick launch and per-user entry points

`--launch-secondary` opens a small progress/cancel window and uses the same controller, serialized gate and CommandJournal as the main UI. Existing confirmation remains unless the user explicitly saved 이 전용 프로필의 빠른 실행 허용 for the current verified profile, installations and policy scope. The choice defaults to OFF and grants launch convenience only. A changed scope falls back to confirmation; invalid policy, ownership, installations or unfinished commands remain blocked and visible in the main window.

The current-user/session instance broker forwards an explicit request to the existing application. It reserves one in-flight request, drops duplicate/busy requests without deferred replay and never creates a parallel controller for a forwarded click. Cancellation before dispatch invalidates intent. After dispatch the process retains the controller until command/journal and fresh owned-box observation settle; uncertain results remain in the same visible recovery UI. A settled successful standalone launch closes its progress window and exits without leaving a hidden poller. Existing owned processes are reported in SIDETALK; external KakaoTalk windows are not activated.

Desktop and Start Menu links use the current executable plus `--launch-secondary`. Login startup adds the fixed `--login-startup` flag, has an independent default-OFF choice and is admitted only in the current interactive, non-SYSTEM, non-elevated user session. Typed Unicode Shell Link COM properties keep the executable path and fixed arguments separate. A manifest, unique description and verified bytes/target/arguments bind the managed links. Edited or unowned collisions are preserved; updates and removals affect only proven-owned links. Atomic file publication and rollback retain incomplete or failed restoration evidence instead of silently re-enabling startup.

## Administrator-rights drift and update evidence

An owned box's missing/empty or single `n` DropAdminRights remains a strict execution block. After verifying ownership and the native storage root, read-only inspection can report the actual explicit/effective values and verified box processes without reconciling commands. Exact KakaoTalk.exe or unknown processes block repair. A reviewed repair changes only that existing box's DropAdminRights to `y`, with fresh explicit approval, the shared command lock, backup, official SbieIni and file/API reinspection; prior automatic consent never approves this additional security change. Other policy differences remain blockers. Version evidence reads only an exact API-identified physical KakaoTalk executable, with no account-store search or guessed virtualization mapping. Official host update guidance and unsupported box-copy replacement are distinguished in [ADMIN-RIGHTS-RECOVERY.md](docs/ADMIN-RIGHTS-RECOVERY.md).

Verified helper-only residuals have a separate one-time cleanup-and-repair approval; ordinary Stop remains blocked by the policy mismatch. Only a single restorable DropAdminRights difference with every other strict rule safe, explicit AutoDelete=n/NeverDelete=y/AutoRecover=n, no management UI and no unfinished work is eligible. The owned-box official Start.exe terminate command records intent before creation and requires known exit code zero plus fresh empty within bounded polling. Every stage revalidates the approved scope before reusing SetupRecovery for the same single repair; new Kakao, unknown observations or scope/UI races prevent configuration writes. Interrupted or unknown-exit cleanup never silently replays or becomes successful. See [RESIDUAL-BOX-RECOVERY.md](docs/RESIDUAL-BOX-RECOVERY.md).

## Secondary appearance

The third tab previews current RGB color and a short palette while preserving an existing choice. Saving changes only the color prefix of the owned box's official `BorderColor`, retaining mode, width and other verified fields. Sandboxie 1.18.4 stores that prefix as BGR: existing `#21685B,ttl,4` therefore previews RGB `#5B6821`. This conversion follows the pinned [SandMan appearance source](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/SandboxiePlus/SandMan/Windows/OptionsGeneral.cpp).

Routine four-second inspections retain the enabled state of the third tab's consent, shortcut and appearance controls. Explicit reload or save requests reserve their work before waiting for an active inspection to finish, then perform the existing settings read or verified save. Competing requests remain disabled during that explicit work; minimized, closed and restore-required windows cannot dispatch a waiting settings request.

The optional exact alias 보조 계정 uses `BoxAlias`; leaving it unselected preserves the existing alias. `BoxNameTitle=y` retains Sandboxie's own title behavior. SandMan must run to draw its [official border](https://sandboxie-plus.github.io/sandboxie-docs/Content/BorderColor/); custom title bars and global alias-display policy can limit the label according to [Sandboxie's official title implementation](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/Sandboxie/core/dll/guititle.c). SIDETALK does not add an external overlay, inject a DLL, patch KakaoTalk or read messages. Visual identification does not prove isolation or account identity.

Appearance writes require a verified empty owned box, the shared command lock, recorded intent/backup and file/API reinspection. They neither terminate management windows nor restore an entire INI. A partial change remains blocked; an explicit retry continues the original bounded selection. Source-level and synthetic checks, screen comparisons and unverified actual Sandboxie display behavior are recorded separately in [VERIFICATION.md](docs/VERIFICATION.md).

## Installer window

`SIDETALK-Setup.exe` uses the CycleArc/WebPier product-family setup window on the existing Inno Setup 6.7.3 engine. `installer/SetupTheme.iss` is the presentation layer shared with WebPier; `installer/SIDETALK.iss` keeps every installation policy. The window is a 600 × 340 frameless charcoal (#141416) surface with soft amber, magenta and teal light rising from the bottom edge, Windows 11 rounded DWM corners, custom minimize/close glyphs, thin translucent panels, one light-blue (#8DBBFF) progress accent and Inno's built-in dark Polar buttons. The SIDETALK lime two-bubble mark (from `Sidetalk.ico`) is the brand mark and confirmation logo.

Pages map to Inno's real states: confirmation (install, update or same-version reinstall, the fixed install location, the 바탕 화면 바로가기 option bound to Inno's own `desktopicon` task, and a note that KakaoTalk and Sandboxie are not closed), preparing, progress (Inno's status line and real file progress, never an estimate), completion (the `[Run]` entry as **SIDETALK 실행**, hidden for `/SIDETALKNOLAUNCH`) and failure (the exact PrepareToInstall reason plus the setup log path, scrolling when longer than the panel, then Close with exit code 7). Empty space drags the window; the close glyph means Cancel with Inno's confirmation, or Finish on the last page. Silent modes, exit codes and message boxes are Inno's own.

Windows High Contrast (and `/NOSTYLE`) disables the custom style: the same compact layout appears in a normal system frame with system colors and no glow. The PNG art is rendered by `scripts/generate-setup-art.ps1` (a .NET port of WebPier's generator, pixel-identical for the shared images); it is never edited by hand.
