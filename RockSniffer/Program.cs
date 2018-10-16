﻿using RockSniffer.Addons;
using RockSniffer.Configuration;
using RockSnifferLib.Cache;
using RockSnifferLib.Events;
using RockSnifferLib.Logging;
using RockSnifferLib.RSHelpers;
using RockSnifferLib.Sniffing;
using RockSnifferLib.SysHelpers;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RockSniffer
{
    class Program
    {
        internal const string version = "0.1.3";

        internal static ICache cache;
        internal static Config config;

        internal static Process rsProcess;

        public static Random random = new Random();

        internal static string cachedir = AppDomain.CurrentDomain.BaseDirectory + "cache";

        private static AddonService addonService;
        private Image defaultAlbumCover = new Bitmap(256, 256);

        private RSMemoryReadout memReadout = new RSMemoryReadout();
        private SongDetails details = new SongDetails();

        static void Main(string[] args)
        {
            Program p = new Program();
            p.Initialize();

            //Keep running even when rocksmith disappears
            while (true)
            {
                try
                {
                    p.Run();
                }
                catch (Exception e)
                {
                    //Catch all exceptions that are not handled and log
                    Logger.LogError("Encountered unhandled exception: {0}\r\n{1}", e.Message, e.StackTrace);
                    throw e;
                }
            }
        }

        public void Initialize()
        {
            //Set title and print version
            Console.Title = string.Format("RockSniffer {0}", version);
            Logger.Log("RockSniffer {0} ({1}bits)", version, CustomAPI.Is64Bits() ? "64" : "32");

            //Initialize and load configuration
            config = new Config();
            try
            {
                config.Load();
            }
            catch (Exception e)
            {
                Logger.LogError("Could not load configuration: {0}\r\n{1}", e.Message, e.StackTrace);
                throw e;
            }

            //Transfer logging options
            Logger.logStateMachine = config.debugSettings.debugStateMachine;
            Logger.logCache = config.debugSettings.debugCache;
            Logger.logFileDetailQuery = config.debugSettings.debugFileDetailQuery;
            Logger.logMemoryReadout = config.debugSettings.debugMemoryReadout;
            Logger.logSongDetails = config.debugSettings.debugSongDetails;
            Logger.logSystemHandleQuery = config.debugSettings.debugSystemHandleQuery;

            //Initialize cache
            cache = new FileCache(cachedir);

            //Create directories
            Directory.CreateDirectory("output");

            //Enable addon service if configured
            if (config.addonSettings.enableAddons)
            {
                try
                {
                    addonService = new AddonService(config.addonSettings.ipAddress, config.addonSettings.port);
                }
                catch (SocketException e)
                {
                    Logger.LogError("Please verify that the IP address is valid and the port is not already in use");
                    Logger.LogError("Could not start addon service: {0}\r\n{1}", e.Message, e.StackTrace);
                }
                catch (Exception e)
                {
                    Logger.LogError("Could not start addon service: {0}\r\n{1}", e.Message, e.StackTrace);
                }
            }
        }

        public void Run()
        {
            //Clear output / create output files
            ClearOutput();
            Logger.Log(string.Format("Waiting for rocksmith on {0}", Environment.OSVersion.Platform));

            //Loop infinitely trying to find rocksmith process
            while (true)
            {
                Logger.Log("DoFindProcess");
                var processes = Process.GetProcessesByName("Rocksmith2014");

                //Sleep for 1 second if no processes found
                if (processes.Length == 0)
                {
                    Thread.Sleep(1000);
                    continue;
                }

                //Select the first rocksmith process and open a handle
                rsProcess = processes[0];

                if (rsProcess.HasExited || (!CustomAPI.IsRunningOnMono() && !rsProcess.Responding))
                {
                    Thread.Sleep(1000);
                    continue;
                }

                break;
            }

            Logger.Log("Rocksmith found! Sniffing...");

            //Initialize file handle reader and memory reader
            Sniffer sniffer = new Sniffer(rsProcess, cache);

            //Listen for events
            sniffer.OnSongChanged += Sniffer_OnCurrentSongChanged;
            sniffer.OnMemoryReadout += Sniffer_OnMemoryReadout;

            //Inform AddonService
            if (config.addonSettings.enableAddons && addonService != null)
            {
                addonService.SetSniffer(sniffer);
            }

            while (true)
            {
                if (rsProcess == null || rsProcess.HasExited)
                {
                    break;
                }
                OutputDetails();

                //GOTTA GO FAST
                Thread.Sleep(1000);

                if (random.Next(0, 100) > 99)
                {
                    Console.WriteLine("*sniff sniff*");
                }
            }

            sniffer.Stop();

            //Clean up as best as we can
            rsProcess.Dispose();
            rsProcess = null;

            Logger.Log("This is rather unfortunate, the Rocksmith2014 process has vanished :/");
        }

        private void Sniffer_OnMemoryReadout(object sender, OnMemoryReadoutArgs args)
        {
            memReadout = args.memoryReadout;
        }

        private void Sniffer_OnCurrentSongChanged(object sender, OnSongChangedArgs args)
        {
            details = args.songDetails;

            //Write album art
            if (details.albumArt != null)
            {
                WriteImageToFileLocking("output/album_cover.jpeg", details.albumArt);
            }
            else
            {
                WriteImageToFileLocking("output/album_cover.jpeg", defaultAlbumCover);
            }
        }

        public static string FormatTime(float lengthTime)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Ceiling(lengthTime));
            return t.ToString(config.formatSettings.timeFormat);
        }

        public static string FormatPercentage(double frac)
        {
            return string.Format(config.formatSettings.percentageFormat, frac * 100d);
        }

        private void OutputDetails()
        {
            //TODO: remember state of each file and only update the ones that have changed!
            foreach (OutputFile of in config.outputSettings.output)
            {
                //Clone the output text format so we can replace strings in it without changing the original
                string outputtext = (string)of.format.Clone();

                //Replace strings from song details
                outputtext = outputtext.Replace("%SONG_ID%", details.songID);
                outputtext = outputtext.Replace("%SONG_ARTIST%", details.artistName);
                outputtext = outputtext.Replace("%SONG_NAME%", details.songName);
                outputtext = outputtext.Replace("%SONG_ALBUM%", details.albumName);
                outputtext = outputtext.Replace("%ALBUM_YEAR%", details.albumYear.ToString());
                outputtext = outputtext.Replace("%SONG_LENGTH%", FormatTime(details.songLength));

                //Toolkit details
                if (details.toolkit != null)
                {
                    outputtext = outputtext.Replace("%TOOLKIT_VERSION%", details.toolkit.version);
                    outputtext = outputtext.Replace("%TOOLKIT_AUTHOR%", details.toolkit.author);
                    outputtext = outputtext.Replace("%TOOLKIT_PACKAGE_VERSION%", details.toolkit.package_version);
                    outputtext = outputtext.Replace("%TOOLKIT_COMMENT%", details.toolkit.comment);
                }

                //If this output contained song detail information
                if (outputtext != of.format)
                {
                    //And our current song details are not valid
                    if (!details.IsValid())
                    {
                        //Output nothing
                        outputtext = "";
                    }
                }

                //Replace strings from memory readout
                outputtext = outputtext.Replace("%SONG_TIMER%", FormatTime(memReadout.songTimer));
                outputtext = outputtext.Replace("%NOTES_HIT%", memReadout.totalNotesHit.ToString());
                outputtext = outputtext.Replace("%CURRENT_STREAK%", (memReadout.currentHitStreak - memReadout.currentMissStreak).ToString());
                outputtext = outputtext.Replace("%HIGHEST_STREAK%", memReadout.highestHitStreak.ToString());
                outputtext = outputtext.Replace("%NOTES_MISSED%", memReadout.totalNotesMissed.ToString());
                outputtext = outputtext.Replace("%TOTAL_NOTES%", memReadout.TotalNotes.ToString());
                outputtext = outputtext.Replace("%CURRENT_ACCURACY%", FormatPercentage((memReadout.totalNotesHit > 0 && memReadout.TotalNotes > 0) ? ((double)memReadout.totalNotesHit / (double)memReadout.TotalNotes) : 0));

                //Write to output
                WriteTextToFileLocking("output/" + of.filename, outputtext);
            }
        }

        private void ClearOutput()
        {
            //Clear all output files
            foreach (OutputFile of in config.outputSettings.output)
            {
                //Write to output
                WriteTextToFileLocking("output/" + of.filename, "");
            }

            //Clear album art
            WriteImageToFileLocking("output/album_cover.jpeg", defaultAlbumCover);
        }

        private void WriteImageToFileLocking(string file, Image image)
        {
            //If the file doesn't exist, create it by writing an empty string into it
            if (!File.Exists(file))
            {
                File.WriteAllText(file, "");
            }

            try
            {
                //Open a file stream, write access, no sharing
                using (FileStream fstream = new FileStream(file, FileMode.Truncate, FileAccess.Write, FileShare.None))
                {
                    image.Save(fstream, ImageFormat.Jpeg);
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Unable to write to file {0}: {1}\r\n{2}", file, e.Message, e.StackTrace);
            }
        }

        private void WriteTextToFileLocking(string file, string contents)
        {
            //If the file doesn't exist, create it by writing an empty string into it
            if (!File.Exists(file))
            {
                File.WriteAllText(file, "");
            }

            //Encode with UTF-8
            byte[] data = Encoding.UTF8.GetBytes(contents);

            //Write to file
            WriteToFileLocking(file, data);
        }

        private void WriteToFileLocking(string file, byte[] contents)
        {
            try
            {
                //Open a file stream, write access, read only sharing
                using (FileStream fstream = new FileStream(file, FileMode.Truncate, FileAccess.Write, FileShare.Read))
                {
                    //Write to file

                    fstream.Write(contents, 0, contents.Length);
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Unable to write to file {0}: {1}\r\n{2}", file, e.Message, e.StackTrace);
            }
        }
    }
}
