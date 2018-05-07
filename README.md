# SteamDB Updater *(and IRC bot)* [![Build Status](https://travis-ci.com/SteamDatabase/SteamDatabaseBackend.svg)](https://travis-ci.com/SteamDatabase/SteamDatabaseBackend)

The application that keeps SteamDB up to date with the latest changes, additionally it runs an IRC bot and announces various Steam stuff in #steamdb and #steamdb-announce on FreeNode.

## Requirements
[.NET Core](https://dot.net)

## IRC

We use [ZNC](http://znc.in) in front of our IRC bot to handle reconnections, staying in channels, flood protection and stuff like that.

There are some modules that are particularly useful to have:

* [keepnick](http://wiki.znc.in/Keepnick)
* [kickrejoin](http://wiki.znc.in/Kickrejoin)
* [stickychan](http://wiki.znc.in/Stickychan)
* [prioritysend](https://github.com/xPaw/znc-prioritysend) *(custom module)*

## Reporting issues
You can report issues in our main repository [over here](https://github.com/SteamDatabase/SteamDatabase).

## License
Use of SteamDB Updater is governed by a BSD-style license that can be found in the LICENSE file.
