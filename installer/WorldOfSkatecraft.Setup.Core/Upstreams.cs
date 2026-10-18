namespace WorldOfSkatecraft.Setup.Core;

/// <summary>
/// Every upstream the setup fetches, each pinned to one revision. Nothing here is redistributed in
/// the package: the setup downloads each one from its authors' own source at install time.
/// </summary>
public static class Upstreams
{
    /// <summary>The Skate 3 Rust Engine, whose converters turn the player's own Skate 3 into assets.</summary>
    public const string EngineUrl = "https://github.com/SK8-ENGINE/skate-3-rust-engine";

    /// <summary>The engine revision the client's `skate-host` dependency and the converter share.</summary>
    public const string EngineRev = "cb79689";

    /// <summary>The converter entry point, at the revision whose tools match <see cref="EngineRev"/>.</summary>
    public const string ConverterUrl =
        "https://raw.githubusercontent.com/Kimmo3223/2010-rust-rewrite-mashup/" +
        "adb684e78ec07654f00e2a9da88d32ae02cc68bd/skate/converter/iw4l_skate_convert.py";

    /// <summary>vgmstream, which decodes the Skate 3 sounds. A missing one costs only the sounds.</summary>
    public const string VgmstreamUrl =
        "https://github.com/vgmstream/vgmstream/releases/download/r2117/vgmstream-win64.zip";

    /// <summary>vmangos-deploy, whose build context carries the vmangos source and Dockerfile.</summary>
    public const string ServerUrl = "https://github.com/mserajnik/vmangos-deploy";

    /// <summary>
    /// The vmangos-deploy revision this setup is written against: a version-1 checkout. The relaunch
    /// of 2026-10-04 replaced the repository's history *and* made every image check
    /// `VMANGOS_DEPLOY_VERSION` at startup, so a pre-relaunch checkout with the relaunch's floating
    /// tags stops both at the map extraction and at every later start. An older install is moved to
    /// this revision in place, keeping its database volume and extracted maps.
    /// </summary>
    public const string ServerRev = "e9ba3fad0161ff277d5adfbc5b66e6b8114cb18b";

    /// <summary>The deploy version the revision above and the images below agree on.</summary>
    public const string DeployVersion = "1";

    /// <summary>
    /// vmangos-deploy's server image, by digest rather than by tag: its tags follow whatever the
    /// deploy repository is at, and an image of another version than the checkout stops at start.
    /// </summary>
    public const string ServerImage =
        "ghcr.io/mserajnik/vmangos-server@sha256:" +
        "ad631ae8c71b648114b67ad9ef716f74f8d43e9662bc0fe087c5e3d276acc717";

    /// <summary>vmangos-deploy's database image, by digest for the same reason.</summary>
    public const string DatabaseImage =
        "ghcr.io/mserajnik/vmangos-database@sha256:" +
        "a2f889c73e313dc5a124468d325ab71a238fe61a285b4e882e310197d8d6efea";

    /// <summary>The vmangos (core) commit <c>setup/vmangos/skatecraft.patch</c> is made against.</summary>
    public const string VmangosRev = "0e3ff01e76d4758e8a7c3108b2717cc785ed56fa";

    /// <summary>The vgmangos client build the server is built for.</summary>
    public const string ClientBuild = "5875";

    /// <summary>The Compose project name, so the server is one Docker project whatever the path is.</summary>
    public const string ProjectName = "world-of-skatecraft";

    /// <summary>The local repository the patched server image is tagged into.</summary>
    public const string PatchedImageRepository = "world-of-skatecraft/vmangos-server";

    /// <summary>
    /// The vgmangos-deploy default database credentials, which are also what
    /// <c>tools/make_account.py</c> uses. The server's database is a local, single-machine one.
    /// </summary>
    public const string DatabaseUser = "mangos";

    /// <summary>See <see cref="DatabaseUser"/>.</summary>
    public const string DatabasePassword = "mangos";

    /// <summary>See <see cref="DatabaseUser"/>.</summary>
    public const string DatabaseRootPassword = "password";

    /// <summary>The patched image's tag: the skatecraft patch's and the server Dockerfile's hashes.</summary>
    public static string PatchedImageTag(string patchAndDockerfileHash12) =>
        $"{ClientBuild}-{patchAndDockerfileHash12}";

    /// <summary>The patched image reference for a given patch+Dockerfile hash.</summary>
    public static string PatchedImage(string patchAndDockerfileHash12) =>
        $"{PatchedImageRepository}:{PatchedImageTag(patchAndDockerfileHash12)}";
}
