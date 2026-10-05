# SIDETALK · 사이드톡

<!-- impeccable:product-schema 1 -->

## Platform

Windows 11 native desktop (WPF). The skill's web/mobile platform enum does not describe this application.

## Stack

Delegated explicitly by the user: C# / .NET 10 LTS / WPF. SDK selection follows global.json; framework-dependent builds require the .NET 10 Desktop Runtime x64. No web server or account service.

## Users and purpose

A Windows user keeping the existing official KakaoTalk main account running, while operating a second official KakaoTalk instance in a dedicated persistent Sandboxie Plus box.

## Workflow and constraints

Korean GUI: prerequisites, setup guidance, launch, stop, process state. No automatic data deletion. Never control another box or the main KakaoTalk. Never copy/read account or chat data. Login and device verification happen in the official app. No app patching, custom hooking, handle unlocking, authentication bypass, or security-feature disabling. Installation and real-account tests require explicit confirmation. The app presents an explicit first approval for its narrow setup/repair scope; later recurrence repair requires stored opt-in consent for the same verified scope. Process existence never proves login success.

Prepared users can enter through a per-user 보조 카카오톡 실행 shortcut and a small cancellable launcher, using the same verification, controller gate and durable command journal. Skipping the existing launch confirmation requires separate default-OFF consent for the exact profile/install/policy scope. Current-user Windows login startup is another independent default-OFF choice; it creates no SYSTEM, elevated or scheduled-task execution and performs no KakaoTalk automatic login. Failed or unfinished operations return to visible diagnostics. Existing owned processes are reported without launching another instance or activating other applications.

The 실행 · 식별 tab offers owned-shortcut creation/removal and a color preview. Secondary-window appearance uses only the official owned-box BorderColor and optional exact 보조 계정 alias, preserving existing unselected choices. SandMan and the target application's title behavior determine the actual external display. Identification is a convenience, never evidence of account identity or isolation. Profile/account/storage and interrupted records remain intact; settings restoration failures preserve their evidence and fail closed for the affected permission.

## Brand commitments

SIDETALK (사이드톡), tidy Korean interface. User explicitly delegated detailed design and order of work; no additional aesthetic interview is required.

## Evidence

2026-09-15 inventory: Windows 11 Pro 10.0.26200 x64; KakaoTalk 26.7.1.5263 running; Sandboxie not found. Live account compatibility is unverified.

## Product principles

- Block execution when isolation cannot be established.
- Keep process observations and user-confirmed account behavior distinct.
- Persist the secondary environment; never offer automatic reset or cleanup.
- Make every system change concrete and reviewable before consent.
