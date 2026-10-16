@echo off
rem Launcher for the Streaming Aggregation release package.
rem
rem The application payload lives in the app\ subdirectory and MUST stay
rem there: the CLR probes only the apphost directory, the Win32 side-by-side
rem loader resolves the embedded manifest against the exe directory, and WinRT
rem activation resolves against the process directory. Spreading the payload
rem over sibling folders produces a process that starts, reports Responding,
rem owns a window handle, and never draws a visible window.
rem
rem This file is the only thing allowed at the release root. Keeping it ASCII
rem on purpose: cmd.exe decodes a .bat in the console OEM/ANSI code page, and
rem this file is run from a fresh double-click, not from a script host that
rem could negotiate UTF-8.
rem
rem /D sets the child's working directory to the payload folder so that any
rem relative path the app resolves against its working directory behaves the
rem same as if the exe had been double-clicked in place.

start "" /D "%~dp0app" "%~dp0app\StreamingAggregation.App.exe"
