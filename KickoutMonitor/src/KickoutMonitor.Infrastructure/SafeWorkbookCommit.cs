using System.Security.Cryptography;
namespace KickoutMonitor.Infrastructure;

public static class SafeWorkbookCommit
{
    public static string Digest(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    // Copy the backup first; never rename the only original into .bak.
    // The injected move is a fault seam for filesystem partial-failure tests.
    public static void Commit(string prepared, string destination, string expectedOriginal,
        Action<string, string>? move = null)
    {
        var backup = destination + ".bak";
        var backupTemp = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var preparedDigest = Digest(prepared);
        var moveStarted = false;
        try
        {
            using (var original = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (Convert.ToHexString(SHA256.HashData(original)) != expectedOriginal)
                    throw new IOException("보고서가 업데이트 도중 변경되었습니다. 다시 시도하세요.");
                original.Position = 0;
                using var output = new FileStream(backupTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                original.CopyTo(output); output.Flush(true);
            }
            if (Digest(backupTemp) != expectedOriginal) throw new IOException("백업 검증 실패. 원본은 변경하지 않았습니다.");
            File.Move(backupTemp, backup, true);
            moveStarted = true;
            (move ?? ((source, target) => File.Move(source, target, true)))(prepared, destination);
            if (!File.Exists(destination) || Digest(destination) != preparedDigest)
                throw new IOException("업데이트 파일 검증에 실패했습니다.");
        }
        catch (Exception error)
        {
            var recovery = "원본은 유지되었습니다.";
            if (moveStarted)
            {
                try
                {
                    if (!File.Exists(destination))
                    { File.Copy(backup, destination, false); recovery = "원본을 백업에서 복구했습니다."; }
                    else if (Digest(destination) == expectedOriginal) recovery = "원본은 유지되었습니다.";
                    else recovery = "대상 파일이 존재합니다. 백업과 비교해 확인하세요.";
                }
                catch (Exception restoreError) { recovery = "자동 복구 실패: " + restoreError.Message; }
            }
            throw new IOException($"{recovery} 백업: {backup}. " +
                (File.Exists(prepared) ? $"완성본 보존: {prepared}. " : "") +
                $"오류 0x{error.HResult:X8}: {error.Message}", error);
        }
        finally { if (File.Exists(backupTemp)) File.Delete(backupTemp); }
    }
}
