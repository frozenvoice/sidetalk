# SIDETALK · 사이드톡

<!-- impeccable:product-schema 1 -->

## Platform

Windows 11 native desktop (WPF). The skill's web/mobile platform enum does not describe this application.

## Stack

Delegated explicitly by the user: C# / .NET 8 / WPF. Existing SDK and desktop runtime are installed. No web server or account service.

## Users and purpose

A Windows user keeping the existing official KakaoTalk main account running, while operating a second official KakaoTalk instance in a dedicated persistent Sandboxie Plus box.

## Workflow and constraints

Korean GUI: prerequisites, setup guidance, launch, stop, process state. No automatic data deletion. Never control another box or the main KakaoTalk. Never copy/read account or chat data. Login and device verification happen in the official app. No app patching, custom hooking, handle unlocking, authentication bypass, or security-feature disabling. Installation, system configuration changes, and real-account tests require explicit confirmation. Process existence never proves login success.

## Brand commitments

SIDETALK (사이드톡), tidy Korean interface. User explicitly delegated detailed design and order of work; no additional aesthetic interview is required.

## Evidence

2026-09-15 inventory: Windows 11 Pro 10.0.26200 x64; KakaoTalk 26.7.1.5263 running; Sandboxie not found. Live account compatibility is unverified.

## Product principles

- Block execution when isolation cannot be established.
- Keep process observations and user-confirmed account behavior distinct.
- Persist the secondary environment; never offer automatic reset or cleanup.
- Make every system change concrete and reviewable before consent.
