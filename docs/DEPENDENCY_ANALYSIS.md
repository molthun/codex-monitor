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
* **Stability Impact**: **Very High**. FanControl is no longer required as the widget's sensor source. It remains useful as an optional companion app for users who want active fan-curve control.

---

## 3. .NET 10 SDK & Runtime (Development & Execution)
* **Purpose**: The SDK compiles `CodexBridge` from source, and the runtime executes the compiled `CodexBridge.exe` binary.
* **Alternatives**:
  * **Self-Contained Native AOT Compilation**: Compile the C# bridge project into a **Native AOT (Ahead-of-Time)** self-contained executable.
* **Simplification Effort**: **Low** (requires updating the `.csproj` file with AOT publish properties).
* **Stability Impact**: **High**. The user will no longer need to install the .NET 10 SDK or .NET Runtime on their computer. The bridge compiles into a single, standalone native `.exe` that runs with zero external dependencies.

---

## 4. Git (Background Auto-Updater)
* **Purpose**: The background watcher uses Git to pull code updates and rebuild the bridge when a new version is pushed to GitHub.
* **Alternatives**:
  * **GitHub Release Updater**: Modify the watcher to check the GitHub Release API via PowerShell (`Invoke-RestMethod`), download the latest release ZIP, and extract it to the target folder.
* **Simplification Effort**: **Medium** (requires rewriting the updater to use ZIP extraction instead of git pull).
* **Stability Impact**: **High**. Removes the need for installing Git on the client machine. Git would only be needed by developers, not end-users.

---

## Summary of Optimization Options

| Dependency | Current Role | Recommendation for Stability & Simplicity | Effort |
| :--- | :--- | :--- | :--- |
| **.NET 10 SDK/Runtime** | Code compilation & execution | **Native AOT (Self-Contained Executable)**: Distribute the compiled `.exe` directly. Eliminates .NET install requirement. | **Low** |
| **Git** | Code pulls & updates | **ZIP Archive Release API**: Download latest ZIPs from GitHub instead of running `git pull`. Eliminates Git client requirement. | **Medium** |
| **LibreHardwareMonitorLib** | CPU/GPU/Fan RPM data | **Current path**: query hardware directly in `CodexBridge`. FanControl remains optional for fan-curve control. | **Done** |
| **Rainmeter** | Render widget panels | **WPF / WinUI Custom App**: Rebuild the UI as a native C# borderless app. Eliminates Rainmeter requirement. | **High** |
