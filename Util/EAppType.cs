namespace SteamDatabaseBackend
{
    // Reference: Steamworks SDK - /public/steam/steamclientpublic.h
    internal enum EAppType
    {
        Invalid      = 0,
        Game         = 1,
        Application  = 2,
        Tool         = 5,
        Demo         = 3,
        Media        = 8,
        DLC          = 4,
        Guide        = 7,
        Driver       = 10,
        Config       = 9,
        Hardware     = 16,
        Franchise    = 19,
        Video        = 13,
        Plugin       = 14,
        MusicAlbum   = 15,
        Series       = 17,
        Comic        = 20,
        Beta         = 18,
        /* Using legacy AppsTypes ids until steamdb.info database is migrated
        Invalid      = 0x000, // unknown / invalid
        Game         = 0x001, // playable game, default type
        Application  = 0x002, // software application
        Tool         = 0x004, // SDKs, editors & dedicated servers
        Demo         = 0x008, // game demo
        Media        = 0x010, // legacy - was used for game trailers, which are now just videos on the web
        DLC          = 0x020, // down loadable content
        Guide        = 0x040, // game guide, PDF etc
        Driver       = 0x080, // hardware driver updater (ATI, Razor etc)
        Config       = 0x100, // hidden app used to config Steam features (backpack, sales, etc)
        Hardware     = 0x200, // a hardware device (Steam Machine, Steam Controller, Steam Link, etc.)
        Franchise    = 0x400, // A hub for collections of multiple apps, eg films, series, games
        Video        = 0x800, // A video component of either a Film or TVSeries (may be the feature, an episode, preview, making-of, etc)
        Plugin       = 0x1000, // Plug-in types for other Apps
        MusicAlbum   = 0x2000, // "Video game soundtrack album"
        Series       = 0x4000, // Container app for video series
        Comic        = 0x8000, // Comic Book
        Beta         = 0x10000, // this is a beta version of a game
        */
    }
}
