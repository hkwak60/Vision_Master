# Release and backup policy

User instruction, 2026-09-22:

- Back up future completed updates by committing and pushing the relevant source, documentation, and latest executable to https://github.com/hkwak60/Vision_Master.git (the existing origin remote).
- Keep only the latest VisionMaster.exe in KickoutMonitor/publish. Do not create historical release folders, executable backups, PDB files, or validation manifests in publish.
- Build/stage and validate releases outside publish, then replace publish/VisionMaster.exe with the validated latest executable. Preserve the existing Git LFS rule for this executable.
- Use Git/GitHub history for version backups. If a push fails, report that the remote backup is incomplete; do not claim it succeeded.
- Do not include production samples, local review/settings stores, credentials, or temporary build/verification files in release commits.
- The user will clean up the already accumulated publish files and update that folder themselves. Do not delete or rearrange those existing files as part of recording this policy.
