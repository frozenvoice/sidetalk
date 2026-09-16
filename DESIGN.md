# SIDETALK design

## Direction and platform

SIDETALK is a Windows 11 WPF utility for operating a second official KakaoTalk instance in a persistent owned Sandboxie environment. The September 16 redesign follows the user's request for a cooler, more playful interface: a compact transport console, charcoal surfaces, an acid-lime launch control, and a paired conversation mark. This is a flat native interface, not a simulated physical device.

The native title bar, resize/minimize/close controls, standard dialogs and keyboard behavior remain. DWM colors only SIDETALK's own caption; it does not touch other applications. No decorative animation, invented activity, login status or safety score.

## Composition

The wordmark and short Korean introduction lead into two native tabs: 보조 카카오톡 and 초기 설정 · 진단. The primary tab contains one status surface, launch and force-stop controls, refresh feedback, and two compact dependency rows. The diagnostics tab holds full policy wording, owned-environment name, storage path, PIDs, timestamps, checks, JSON export, installation details and first-time setup instructions.

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
- Controls and event handlers both require ActionsPermitted and their CanLaunch/CanStop flag. Busy actions disable competing actions. Policy checks, storage preservation and command lifecycle belong to Core and remain unchanged.

## Interaction and verification

Inspection runs on opening and every four seconds. Manual refresh is optional when an initial inspection already permits launch. Buttons expose hover, pressed, keyboard-focus and disabled states; state text is meaningful without color. Primary/secondary and disabled-text contrast were checked against their actual backgrounds.

The native WPF client area was rendered at default and minimum dimensions. Separate fixture snapshots cover ready, running, blocked, diagnostics, error, launch-failed and stop-residual states; they are explicitly synthetic and cannot prove real account behavior. A distinct read-only render uses the real user environment. Final visual review found the tab labels, dark scrolling controls, complete shell and preserved action feedback ready to ship.
