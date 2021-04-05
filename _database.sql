-- Copyright (c) 2013-present, SteamDB. All rights reserved.
-- Use of this source code is governed by a BSD-style license that can be
-- found in the LICENSE file.

-- This is a partical database structure dump used by SteamDB
-- This structure is not final and can change at any time

SET SQL_MODE = "NO_AUTO_VALUE_ON_ZERO";
SET time_zone = "+00:00";

CREATE TABLE IF NOT EXISTS `Apps` (
  `AppID` int(10) UNSIGNED NOT NULL,
  `AppType` int(10) UNSIGNED NOT NULL DEFAULT 0,
  `Name` varchar(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT 'SteamDB Unknown App',
  `StoreName` varchar(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT '',
  `LastKnownName` varchar(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT '',
  `LastUpdated` datetime NOT NULL DEFAULT current_timestamp(),
  `LastDepotUpdate` datetime DEFAULT NULL,
  PRIMARY KEY (`AppID`) USING BTREE,
  KEY `AppType` (`AppType`),
  KEY `LastUpdated` (`LastUpdated`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `AppsHistory` (
  `ID` int(10) UNSIGNED NOT NULL AUTO_INCREMENT,
  `ChangeID` int(10) UNSIGNED NOT NULL DEFAULT 0,
  `AppID` int(10) UNSIGNED NOT NULL,
  `Time` datetime NOT NULL DEFAULT current_timestamp(),
  `Action` enum('created_app','deleted_app','created_key','removed_key','modified_key','created_info','modified_info','removed_info','added_to_sub','removed_from_sub') CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `Key` smallint(5) UNSIGNED NOT NULL DEFAULT 0,
  `OldValue` longtext COLLATE utf8mb4_bin NOT NULL,
  `NewValue` longtext COLLATE utf8mb4_bin NOT NULL,
  `Diff` longtext COLLATE utf8mb4_bin DEFAULT NULL,
  PRIMARY KEY (`ID`),
  KEY `AppID` (`AppID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin ROW_FORMAT=COMPRESSED;

CREATE TABLE IF NOT EXISTS `AppsInfo` (
  `AppID` int(10) UNSIGNED NOT NULL,
  `Key` smallint(5) UNSIGNED NOT NULL,
  `Value` longtext COLLATE utf8mb4_bin NOT NULL,
  PRIMARY KEY (`AppID`,`Key`),
  KEY `Key` (`Key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `Builds` (
  `BuildID` int(10) UNSIGNED NOT NULL,
  `ChangeID` int(10) UNSIGNED NOT NULL,
  `AppID` int(10) UNSIGNED NOT NULL,
  PRIMARY KEY (`BuildID`),
  KEY `AppID` (`AppID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `Changelists` (
  `ChangeID` int(10) UNSIGNED NOT NULL,
  `Date` datetime NOT NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`ChangeID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `ChangelistsApps` (
  `ID` int(10) UNSIGNED NOT NULL AUTO_INCREMENT,
  `ChangeID` int(10) UNSIGNED NOT NULL,
  `AppID` int(10) UNSIGNED NOT NULL,
  PRIMARY KEY (`ID`),
  UNIQUE KEY `ChangeID` (`ChangeID`,`AppID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `ChangelistsSubs` (
  `ID` int(10) UNSIGNED NOT NULL AUTO_INCREMENT,
  `ChangeID` int(10) UNSIGNED NOT NULL,
  `SubID` int(10) UNSIGNED NOT NULL,
  PRIMARY KEY (`ID`),
  UNIQUE KEY `ChangeID` (`ChangeID`,`SubID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `Depots` (
  `DepotID` int(10) UNSIGNED NOT NULL,
  `Name` varchar(1000) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'SteamDB Unknown Depot',
  `BuildID` int(10) UNSIGNED NOT NULL DEFAULT 0,
  `ManifestID` bigint(20) unsigned NOT NULL DEFAULT 0,
  `LastManifestID` bigint(20) unsigned NOT NULL DEFAULT 0,
  `ManifestDate` datetime DEFAULT NULL,
  `FilenamesEncrypted` tinyint(1) NOT NULL DEFAULT 0,
  `SizeOriginal` bigint(20) UNSIGNED NOT NULL DEFAULT 0,
  `SizeCompressed` bigint(20) UNSIGNED NOT NULL DEFAULT 0,
  `LastUpdated` datetime NOT NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`DepotID`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `DepotsFiles` (
  `DepotID` int(10) UNSIGNED NOT NULL,
  `File` varchar(260) COLLATE utf8mb4_bin NOT NULL,
  `Hash` binary(20) DEFAULT NULL,
  `Size` bigint(20) UNSIGNED NOT NULL,
  `Flags` smallint(5) UNSIGNED NOT NULL,
  PRIMARY KEY (`DepotID`,`File`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `DepotsHistory` (
  `ID` int(10) UNSIGNED NOT NULL AUTO_INCREMENT,
  `ChangeID` int(10) UNSIGNED NOT NULL,
  `ManifestID` bigint(20) UNSIGNED NOT NULL,
  `DepotID` int(10) UNSIGNED NOT NULL,
  `Time` datetime NOT NULL DEFAULT current_timestamp(),
  `Action` enum('added','removed','modified','modified_flags','manifest_change','added_to_sub','removed_from_sub','files_decrypted') CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `File` varchar(260) COLLATE utf8mb4_bin NOT NULL,
  `OldValue` bigint(20) UNSIGNED NOT NULL,
  `NewValue` bigint(20) UNSIGNED NOT NULL,
  PRIMARY KEY (`ID`),
  KEY `DepotID` (`DepotID`),
  KEY `ManifestID` (`ManifestID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `DepotsKeys` (
  `DepotID` int(10) UNSIGNED NOT NULL,
  `Key` varchar(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `Date` datetime NOT NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`DepotID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `ImportantApps` (
  `AppID` int(10) UNSIGNED NOT NULL,
  PRIMARY KEY (`AppID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `ImportantSubs` (
  `SubID` int(10) UNSIGNED NOT NULL,
  PRIMARY KEY (`SubID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `KeyNames` (
  `ID` smallint(5) UNSIGNED NOT NULL AUTO_INCREMENT,
  `Type` tinyint(3) UNSIGNED NOT NULL DEFAULT 0,
  `Name` varchar(90) COLLATE utf8mb4_bin NOT NULL,
  `DisplayName` varchar(120) COLLATE utf8mb4_bin NOT NULL DEFAULT '',
  PRIMARY KEY (`ID`),
  UNIQUE KEY `Name` (`Name`),
  KEY `Type` (`Type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `KeyNamesSubs` (
  `ID` smallint(5) UNSIGNED NOT NULL AUTO_INCREMENT,
  `Type` tinyint(3) UNSIGNED NOT NULL DEFAULT 0,
  `Name` varchar(90) COLLATE utf8mb4_bin NOT NULL,
  `DisplayName` varchar(90) COLLATE utf8mb4_bin NOT NULL DEFAULT '',
  PRIMARY KEY (`ID`),
  UNIQUE KEY `Name` (`Name`),
  KEY `Type` (`Type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `LocalConfig` (
  `ConfigKey` varchar(256) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `Value` mediumtext COLLATE utf8mb4_bin NOT NULL,
  PRIMARY KEY (`ConfigKey`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `PICSTokens` (
  `AppID` int(10) UNSIGNED NOT NULL,
  `Token` bigint(20) UNSIGNED NOT NULL,
  `Date` datetime NOT NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`AppID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `PICSTokensSubs` (
  `SubID` int(10) UNSIGNED NOT NULL,
  `Token` bigint(20) UNSIGNED NOT NULL,
  `Date` datetime NOT NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`SubID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `RSS` (
  `ID` int(10) UNSIGNED NOT NULL AUTO_INCREMENT,
  `Title` varchar(255) COLLATE utf8mb4_bin NOT NULL,
  `Link` varchar(190) COLLATE utf8mb4_bin NOT NULL,
  `Date` datetime NOT NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`ID`),
  UNIQUE KEY `Link` (`Link`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `Subs` (
  `SubID` int(10) UNSIGNED NOT NULL,
  `Name` varchar(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT 'SteamDB Unknown Package',
  `StoreName` varchar(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT '',
  `LastKnownName` varchar(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL DEFAULT '',
  `LastUpdated` datetime NOT NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`SubID`) USING BTREE,
  KEY `LastUpdated` (`LastUpdated`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `SubsApps` (
  `SubID` int(10) UNSIGNED NOT NULL,
  `AppID` int(10) UNSIGNED NOT NULL,
  `Type` enum('app','depot') CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (`SubID`,`AppID`),
  KEY `AppID` (`AppID`),
  KEY `SubID` (`SubID`),
  KEY `Type` (`Type`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `SubsHistory` (
  `ID` int(10) UNSIGNED NOT NULL AUTO_INCREMENT,
  `ChangeID` int(10) UNSIGNED NOT NULL DEFAULT 0,
  `SubID` int(10) UNSIGNED NOT NULL,
  `Time` datetime NOT NULL DEFAULT current_timestamp(),
  `Action` enum('created_sub','deleted_sub','created_key','removed_key','modified_key','created_info','modified_info','removed_info','modified_price','added_to_sub','removed_from_sub') CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `Key` smallint(5) UNSIGNED NOT NULL,
  `OldValue` text COLLATE utf8mb4_bin NOT NULL,
  `NewValue` text COLLATE utf8mb4_bin NOT NULL,
  PRIMARY KEY (`ID`),
  KEY `SubID` (`SubID`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS `SubsInfo` (
  `SubID` int(10) UNSIGNED NOT NULL,
  `Key` smallint(5) UNSIGNED NOT NULL,
  `Value` text COLLATE utf8mb4_bin NOT NULL,
  PRIMARY KEY (`SubID`,`Key`),
  KEY `Key` (`Key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
