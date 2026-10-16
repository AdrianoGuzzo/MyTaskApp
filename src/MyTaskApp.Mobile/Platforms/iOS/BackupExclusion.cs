using Foundation;

namespace MyTaskApp.Mobile;

/// <summary>
/// Tira a pasta de dados do backup do iCloud (ADR-065): restaurada noutro
/// iPhone, ela levaria o id deste dispositivo, e os dois sincronizariam em
/// nome do mesmo. O equivalente do <c>allowBackup="false"</c> do Android.
/// </summary>
internal static class BackupExclusion
{
    public static void Apply(string folder)
    {
        Directory.CreateDirectory(folder);
        var url = NSUrl.FromFilename(folder);
        url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true));
    }
}
