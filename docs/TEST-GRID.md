# Install scenario grid (updater)

Automated, in `tests/Lotr.Updater.Tests/ScenarioGridTests.cs` (plus the older `EngineTests.cs` / `WorkshopTests.cs`).
Each scenario runs the real install engine against a local test HTTP server (`localhost`, random port) and a fake
Bannerlord folder in `%TEMP%\lotr-updater-tests\<id>` (deleted afterwards). The real game and Steam folders are never touched.

Every scenario checks the final state: release files byte-identical, no `.lotr-staging` left, no `*.part` left in the
cache, the player's own files kept, and every player-facing text (plan issues, error messages) free of raw .NET
exception text (`PlayerErrors.LooksTechnical`) and of non-English letters.

| # | Scenario | Test | Expected |
|---|----------|------|----------|
| 1 | Fresh PC | `Fresh_pc_installs_everything` | everything downloaded and installed, next check up to date |
| 2 | Core only, Steam Workshop | `Core_only_from_workshop_copies_core_and_downloads_event` | Core copied from the Workshop (read-only), Event downloaded, no Core download |
| 3 | Core only, local `Modules` | `Core_only_in_local_modules_downloads_only_the_event` | Core untouched, Event downloaded |
| 4 | Core in `Modules` and Workshop | `Core_in_modules_and_workshop_keeps_modules_and_only_informs` | Modules copy kept, Workshop shown as Info only |
| 5 | Event only | `Event_only_installed_downloads_the_core` | Core downloaded through the queue |
| 6 | Both from Workshop | `Both_modules_from_workshop_download_nothing` | zero bytes downloaded |
| 7 | Both local, up to date | `Both_local_and_up_to_date_needs_nothing` | no change, release recorded |
| 8 | Old local versions | `Old_local_versions_are_updated_and_player_files_kept` | updated, `my-notes.txt` kept |
| 9 | Workshop older / newer than release | `Workshop_copy_of_another_version_reuses_identical_files_only` (x2) | identical files copied, different ones downloaded |
| 10 | Unsubscribed between check and install (player report) | `Unsubscribed_between_check_and_install_downloads_instead` | re-check before install switches to downloads, success |
| 11 | Workshop folder vanishes while preparing | `Workshop_folder_vanishing_while_preparing_is_downloaded_on_the_spot` | that file downloaded on the spot, success |
| 12 | Same, on-the-spot fallback off | `Failing_local_source_is_replanned_once_automatically` | service re-plans once, success |
| 13 | Workshop source keeps failing | `Workshop_source_that_keeps_failing_gives_a_calm_message_and_changes_nothing` | calm English message, raw French text only in Details, game folder unchanged, next try works |
| 14 | Steam rewrites a Workshop file (new date) | `Steam_rewriting_a_workshop_file_is_noticed_before_installing` | re-hash before install, downloaded instead |
| 15 | Stale hash cache (same size and date) | `Stale_hash_cache_entry_is_caught_by_the_final_check` | final SHA-256 check catches it, downloaded instead |
| 16 | Player changes `Modules` between check and install | `Player_changes_in_modules_between_check_and_install_are_handled` | deleted / changed files restored, missing Replace target handled |
| 17 | File locked for a moment (antivirus) | `Briefly_locked_file_is_retried_and_the_install_succeeds` | retried with backoff, success |
| 18 | File locked for good (known / unknown program) | `File_locked_for_good_rolls_back_with_a_clear_message` (x2) | rollback, message names the program or says "another program" |
| 19 | Disk full while preparing | `Disk_full_while_preparing_rolls_back_with_a_clear_message` | rollback, "Your disk became full…" |
| 20 | Disk full while downloading | `Disk_full_while_downloading_stops_at_once_without_touching_the_game` | stops at once (no network retries), game folder untouched |
| 21 | Interrupted download, resume | `Interrupted_download_fails_calmly_then_resumes` (+ `EngineTests.Interrupted_download_resumes_with_range`) | calm network message, next try resumes with a Range request |
| 22 | Server unreachable at check | `Unreachable_server_gives_a_calm_check_message` | calm message, detail kept |
| 23 | Game in another Steam library | `Game_in_another_steam_library_uses_that_library_workshop` | Workshop of that library reused |
| 24 | Backup on another drive | `Backup_on_another_drive_is_copied_and_the_update_completes` | backup copied across volumes |
| 25 | Epic / GOG / Xbox (no Workshop) | `Other_stores_install_without_any_workshop` (x3) | no Workshop lookup, install works |
| 26 | Duplicate module copy under another folder name | `Duplicate_copy_under_another_folder_name_is_warned_and_left_alone` | warning, the copy is not touched |
| 27 | Start-up crash report | `Start_up_crash_report_travels_with_the_support_log` | latest `logs\crash-*.txt` included in the support log |
| 28 | Wording | `The_reported_french_exception_becomes_a_calm_english_message`, `Every_install_message_is_friendly` | no raw exception text for any mapped error |
| 29 | Download queue down (Cloudflare 530/502/520, 503, 500, 403, 408, 200 with an HTML page) | `QueueFallbackTests.A_broken_queue_gets_one_more_try_then_the_core_is_downloaded_directly` (x8) | one more try after 2 s, then the Core is downloaded directly, a warning is logged, install succeeds |
| 30 | Download queue never answers | `QueueFallbackTests.A_queue_that_never_answers_is_given_up_after_the_timeout` | per-call timeout (15 s in production), then direct download |
| 31 | No queue (404), busy queue (429), cancel while waiting or while it hangs | `QueueFallbackTests` (4 tests) | 404 = direct at once; 429 still waited for; the player's cancel stops at once |
| 32 | Linux: game in native / Flatpak / Snap Steam, several libraries, old and new `libraryfolders.vdf`, SD card, symlinks, `SteamApps` letter case, broken VDF | `LinuxSteamLocatorTests` (19 tests) | found once, at its real on-disk name, Proton prefix reported |
| 33 | Linux: stale mod file under another letter case | `LinuxSupportTests.With_linux_rules_a_letter_case_copy_of_a_release_file_is_moved_to_the_backup`, `With_windows_rules_letter_case_never_makes_a_copy` | moved to the backup with an Info line (Windows rules: nothing to do) |
| 34 | Linux: disk full / quota / read-only file system, wording | `LinuxSupportTests` | `ENOSPC`, `EDQUOT`, `EROFS` mapped to the calm messages, no "Windows" in Linux texts |
| 35 | `lotr-updater` command line, end to end | `tests/Lotr.Updater.Cli.Tests` + `tools/linux/e2e-local.sh` | exit codes 0/2/3/4/6/7/8/9/10/130 as documented in `docs/LINUX.md` |

## Run

```powershell
cd installer
$env:LOTR_SKIP_SMOKE = '1'
dotnet test LotR.Installer.sln -c Release                                   # everything
dotnet test tests\Lotr.Updater.Tests -c Release --filter FullyQualifiedName~ScenarioGridTests   # the grid only
```

Both target frameworks (net48 and net8.0) run. The grid needs no network beyond `localhost` and takes about 20 s.
