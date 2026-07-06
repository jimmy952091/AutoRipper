using System;
using System.IO;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Sanity checks for the output-library and scratch folders collected in setup.
    /// Folders are less strict than the CLI tools: a not-yet-existing library folder is a
    /// warning, not a hard failure, because it's reasonable to create it. The temp folder
    /// gets an extra check for removable/network drives, which the spec calls out as slow
    /// or unreliable for large scratch writes.
    /// </summary>
    public static class FolderValidator
    {
        public static ValidationResult ValidateOutputFolder(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ValidationResult.Fail("No " + label + " folder set.");
            }

            if (Directory.Exists(path))
            {
                return ValidationResult.Ok("OK — " + path);
            }

            if (!IsWellFormedPath(path))
            {
                return ValidationResult.Fail("That doesn't look like a valid folder path.");
            }

            // Doesn't exist yet, but the path is sane — treat as a warning; the app can
            // create it on demand later (folder creation is idempotent per spec).
            return ValidationResult.Fail("Folder doesn't exist yet — it will be created when needed.");
        }

        public static ValidationResult ValidateTempFolder(string path)
        {
            ValidationResult basic = ValidateOutputFolder(path, "temporary/scratch");
            // If the base check hard-failed on a blank/invalid path, return that as-is.
            if (!basic.Success && !Directory.Exists(path))
            {
                return basic;
            }

            try
            {
                string root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.DriveType == DriveType.Removable)
                    {
                        return ValidationResult.Fail(
                            "Warning: this is on a removable drive. Rips can be tens of GB — a " +
                            "USB stick or external drive may be slow and can be unplugged mid-job. " +
                            "A fast internal drive is strongly recommended.");
                    }
                    if (drive.DriveType == DriveType.Network)
                    {
                        return ValidationResult.Fail(
                            "Warning: this is on a network location. Scratch writes are heavy and " +
                            "network drives are usually much slower. A local internal drive is " +
                            "strongly recommended.");
                    }
                }
            }
            catch
            {
                // DriveInfo can throw on odd paths (UNC without a mapped root, etc.).
                // Not being able to classify the drive isn't itself an error.
            }

            return ValidationResult.Ok("OK — " + path);
        }

        private static bool IsWellFormedPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                return !string.IsNullOrEmpty(full);
            }
            catch
            {
                return false;
            }
        }
    }
}
