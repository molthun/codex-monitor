# CodexMonitor Dependency Analysis

This document provides a systematic analysis of CodexMonitor's current third-party software dependencies, their roles, and potential architectural changes to reduce external software requirements and improve overall stability.

---

## 1. Rainmeter (Desktop UI Engine)
* **Purpose**: Renders the desktop widget interface (rounded bars, charts, text labels, PNG icons) and handles native integration on the desktop (transparency, click-through, position locks).
* **Alternatives**:
  * **Custom GUI App (C# / WPF / WinUI 3)**: We could write a dedicated, borderless C# desktop application that uses transparency, click-through, and anchors directly to the desktop wallpaper coordinates.
* **Simplification Effort**: **High** (requires rebuilding the entire layout and coordinate systems in code).
* **Stability Impact**: **High**. Rainmeter is mature and extremely stable, but it is another software package to install. Writing a custom WPF app gives us full control over rendering and removes Rainmeter entirely.

---

## 2. Hardware Sensor Provider
* **Current implementation**: `CodexBridge` reads CPU/GPU/motherboard fan sensors directly through **LibreHardwareMonitorLib**.
* **Alternatives**:
  * **Direct Hardware Reading via C#**: We can reference the open-source **LibreHardwareMonitorLib** directly inside the C# bridge project. This library can read CPU, GPU, RAM, Disk, and Motherboard Super I/O sensors directly.
* **Simplification Effort**: **Medium** (requires adding LibreHardwareMonitorLib and writing code to initialize and query the hardware sensors inside the bridge).
* **Stability Impact**: **Very High**. CodexMonitor is display-only and leaves fan behavior to the user's BIOS/UEFI, drivers, or existing system tools.

---

## 3. .NET SDK (Development Only)
* **Current implementation**: Users receive a bundled self-contained `CodexBridge.exe`. The .NET SDK is only needed by developers who change bridge source code and rebuild the executable.
  The executable also hosts the WinForms settings wizard via `--settings`.
* **Alternatives**:
  * **Native AOT**: Compile the bridge as a smaller ahead-of-time native executable if LibreHardwareMonitor compatibility allows it.
* **Simplification Effort**: **Done** for self-contained single-file publish; **Medium** if moving to Native AOT later.
* **Stability Impact**: **High**. End users no longer need to install the .NET SDK or runtime on their computer.

---

## 4. GitHub Release Updater (Background Auto-Updater)
* **Current implementation**: The background watcher checks the GitHub Releases API for the latest tag. When it differs from `.local_version`, it downloads that tag's source ZIP (scripts/skin/presets), downloads the precompiled `CodexBridge.exe` from the release assets, preserves the user's local `config.json`, replaces the bridge binary, and refreshes Rainmeter. The binary itself is built by CI (`.github/workflows/release.yml`) on each tag and is not committed to the repo.
* **Stability Impact**: **High**. Git is not required on the client machine. Updates are tied to immutable, versioned releases instead of a moving branch, and the repository stays free of large committed binaries.

---

## Summary of Optimization Options

| Dependency | Current Role | Recommendation for Stability & Simplicity | Effort |
| :--- | :--- | :--- | :--- |
| **.NET SDK** | Developer rebuilds only | **Current path**: distribute the compiled self-contained `.exe`. Eliminates end-user .NET install requirement. | **Done** |
| **GitHub ZIP updater** | Code updates | **Current path**: download latest ZIPs from GitHub instead of invoking Git on the client. Eliminates Git client requirement. | **Done** |
| **LibreHardwareMonitorLib** | CPU/GPU/Fan RPM data | **Current path**: query hardware directly in `CodexBridge`. CodexMonitor displays current state only. | **Done** |
| **Rainmeter** | Render widget panels | **WPF / WinUI Custom App**: Rebuild the UI as a native C# borderless app. Eliminates Rainmeter requirement. | **High** |
