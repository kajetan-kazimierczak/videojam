# Phase 3 Review Findings — Required Fixes

> Prepared by QA following smoke test execution on `phase-3-multi-display-manager`.
> One item is **CRITICAL** (blocks archive). Further items may be added following a full
> code review pass once the critical defect is resolved.

---

## CRITICAL

### 1. `AudioEngine.Load()` throws on a missing `VideoAudio` channel, preventing graceful degradation

**ID:** Phase3-001
**File:** `VideoJam/Engine/AudioEngine.cs` — `Load()`, line 90; `CreateReader()`, lines 176–177
**Discovered by:** Smoke test 6.3

**Summary:**
Smoke test 6.3 requires that deleting a video file after folder load (but before Play) results
in graceful degradation: audio plays, the valid display shows video, the invalid display shows
fallback black, and no crash occurs. The test **fails** — a `MessageBox` error dialog appears
and playback is aborted entirely.

**Root cause:**
`SongScanner` registers every `.mp4` file in the manifest **twice**: once as a `VideoFileManifest`
entry (consumed by `VideoEngine`) and once as an `AudioChannelManifest` of type `VideoAudio`
(consumed by `AudioEngine`). When `OnPlayClicked` runs:

1. `_audioEngine.Load(_manifest, channelSettings)` is called **before** `_videoEngine.LoadAll(...)`.
2. Inside `AudioEngine.Load()`, `CreateReader(channel.File)` is invoked for every audio channel,
   including `VideoAudio` channels.
3. For `.mp4` files, `CreateReader` instantiates `MediaFoundationReader(file.FullName)` directly.
4. `MediaFoundationReader` throws (`0x80070002 — The system cannot find the file specified`)
   when the file does not exist on disk.
5. This exception propagates unhandled through `AudioEngine.Load()` into the `catch (Exception ex)`
   block in `OnPlayClicked`, which shows the error dialog and aborts playback entirely.
6. `VideoEngine.LoadAll()` is **never reached**, so the graceful degradation logic added there
   in this phase is not exercised.

**Note:** The `VideoEngine` fix (moving `player.Play()` inside the `try` block and adding a
general exception catch for pre-buffer failures) is correct and necessary. It is simply
insufficient on its own because `AudioEngine` fails first.

**Observed error dialog:**
> Playback failed:
> The system cannot find the file specified. (0x80070002)

**Required fix:**
`AudioEngine.Load()` must handle missing `VideoAudio` channels gracefully — logging a warning
and skipping the failed channel rather than propagating the exception. The `VideoAudio` audio
track is muted by default in any case (see `OnPlayClicked`:`Muted = ch.Type == AudioChannelType.VideoAudio`),
so its absence is inconsequential to the listener experience.

One approach: wrap `CreateReader` in a try-catch inside the `foreach` loop; on failure, log a
warning and `continue` to the next channel. This mirrors the pattern used in `VideoEngine.Load()`
for the equivalent pre-buffer failure case.

**Acceptance criteria for the fix:**
- Smoke test 6.3 must pass: audio plays; display 0 shows video; display 1 remains black; no crash.
- Existing unit tests (≥ 28) must continue to pass.
- The console log must contain a `LogWarning` (or equivalent diagnostic) identifying the missing
  file and the channel that was skipped.
- No regression in the two-valid-files scenario (smoke tests 6.1 and 6.2).

**Resolution:**
Fixed in `VideoJam/Engine/AudioEngine.cs`. `CreateReader()` is now wrapped in a `try-catch`
inside the channel loop; on failure a `LogWarning` is emitted with the channel ID and filename
and the loop continues to the next channel. The failed reader is never added to `_readers`,
so no resource leak is introduced. `AudioEngine` was also given an `ILogger<AudioEngine>`
constructor parameter (consistent with `VideoEngine`); `MainWindow.xaml.cs` updated accordingly.
Fix reviewed and approved by QA.

**Status: Resolved ✓**
