#region Disclaimer / License
// Copyright (C) 2008, Kenneth Skovhede
// http://www.hexad.dk, opensource@hexad.dk
// 
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
// 
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
// 
#endregion
using System;
using System.Collections.Generic;
using System.Text;

namespace Duplicati.Main
{
    public static class Interface
    {
        public static void Backup(string source, string target, Dictionary<string, string> options)
        {
            using (new Logging.Timer("Backup from " + source + " to " + target))
            {
                FilenameStrategy fns = new FilenameStrategy(options);
                Core.FilenameFilter filter = new Duplicati.Core.FilenameFilter(options);
                bool full = options.ContainsKey("full");

                Backend.IBackendInterface backend = new Backend.BackendLoader(target, options);
                List<BackupEntry> prev = ParseFileList(target, options);

                if (prev.Count == 0)
                    full = true;

                if (!full && options.ContainsKey("full-if-older-than"))
                    full = DateTime.Now > Core.Timeparser.ParseTimeInterval(options["full-if-older-than"], prev[prev.Count - 1].Time);

                using (Core.TempFolder basefolder = new Duplicati.Core.TempFolder())
                {
                    if (!full)
                    {
                        using (new Logging.Timer("Reading incremental data"))
                        {
                            using (Core.TempFile t = new Duplicati.Core.TempFile())
                            {
                                using (new Logging.Timer("Get " + prev[prev.Count - 1].SignatureFile.Filename))
                                    backend.Get(prev[prev.Count - 1].SignatureFile.Filename, t);
                                Compression.Compression.Decompress(t, basefolder);
                            }

                            foreach (BackupEntry be in prev[prev.Count - 1].Incrementals)
                                using (Core.TempFile t = new Duplicati.Core.TempFile())
                                {
                                    using (new Logging.Timer("Get " + be.SignatureFile.Filename))
                                        backend.Get(be.SignatureFile.Filename, t);

                                    using (Core.TempFolder tf = new Duplicati.Core.TempFolder())
                                    {
                                        Compression.Compression.Decompress(t, tf);
                                        using (new Logging.Timer("Full signature merge"))
                                            Main.RSync.RSyncDir.MergeSignatures(basefolder, tf);
                                    }
                                }
                        }
                    }
                    DateTime backuptime = DateTime.Now;

                    RSync.RSyncDir dir = new Duplicati.Main.RSync.RSyncDir(source, basefolder);
                    using (new Logging.Timer("Scanning folder for changes"))
                        dir.CalculateDiffFilelist(full, filter);

                    int volumesize = options.ContainsKey("volsize") ? int.Parse(options["volsize"]) : 5;
                    volumesize = Math.Max(1, volumesize);
                    //dir.CreateDeltas();

                    using (new Logging.Timer("Creating folders for signature file"))
                        dir.CreateFolders();

                    using (Core.TempFile zf = new Duplicati.Core.TempFile())
                    {
                        List<string> folders = Core.Utility.EnumerateFolders(dir.NewSignatures);
                        List<string> files = Core.Utility.EnumerateFiles(dir.NewSignatures);
                        if (System.IO.File.Exists(dir.DeletedFolders))
                            files.Add(dir.DeletedFolders);
                        if (System.IO.File.Exists(dir.DeletedFiles))
                            files.Add(dir.DeletedFiles);

                        Compression.Compression.Compress(files, folders, zf, dir.m_targetfolder);
                        using (new Logging.Timer("Writing remote signatures"))
                            backend.Put(fns.GenerateFilename("duplicati", BackupEntry.EntryType.Signature, full, backuptime) + ".zip", zf);
                    }

                    int vol = 0;
                    while (dir.HasChanges || vol == 0)
                        using (Core.TempFile zf = new Duplicati.Core.TempFile())
                        {
                            using (new Logging.Timer("Delta generation " + (vol + 1).ToString()))
                                dir.CreateDeltas(zf, volumesize * 1024 * 1024);
                            //Compression.Compression.Compress(dir.NewDeltas, zf, dir.m_targetfolder);
                            using (new Logging.Timer("Writing delta file " + (vol + 1).ToString()))
                                backend.Put(fns.GenerateFilename("duplicati", BackupEntry.EntryType.Content, full, backuptime, vol++) + ".zip", zf);
                        }


                    using (Core.TempFile mf = new Duplicati.Core.TempFile())
                    using (new Logging.Timer("Writing manifest"))
                        backend.Put(fns.GenerateFilename("duplicati", BackupEntry.EntryType.Manifest, full, backuptime) + ".manifest", mf);

                }
            }
        }

        public static void Restore(string source, string target, Dictionary<string, string> options)
        {
            using (new Logging.Timer("Restore from " + source + " to " + target))
            {
                string specificfile = options.ContainsKey("file-to-restore") ? options["file-to-restore"] : "";
                string specifictime = options.ContainsKey("restore-time") ? options["restore-time"] : "now";
                Backend.IBackendInterface backend = new Backend.BackendLoader(source, options);

                if (string.IsNullOrEmpty(specifictime))
                    specifictime = "now";

                List<BackupEntry> backups = ParseFileList(source, options);
                if (backups.Count == 0)
                    throw new Exception("No backups found at remote location");

                DateTime timelimit = Core.Timeparser.ParseTimeInterval(specifictime, DateTime.Now);

                BackupEntry bestFit = backups[0];
                List<BackupEntry> additions = new List<BackupEntry>();
                foreach (BackupEntry be in backups)
                    if (be.Time < timelimit)
                    {
                        bestFit = be;
                        foreach (BackupEntry bex in be.Incrementals)
                            if (bex.Time <= timelimit)
                                additions.Add(bex);

                    }

                using (Core.TempFolder basefolder = new Duplicati.Core.TempFolder())
                {
                    using (Core.TempFile basezip = new Duplicati.Core.TempFile())
                    {
                        using (new Logging.Timer("Get " + bestFit.SignatureFile.Filename))
                            backend.Get(bestFit.SignatureFile.Filename, basezip);
                        Compression.Compression.Decompress(basezip, basefolder);
                    }

                    foreach (BackupEntry vol in bestFit.ContentVolumes)
                        using (Core.TempFile basezip = new Duplicati.Core.TempFile())
                        {
                            using (new Logging.Timer("Get " + vol.Filename))
                                backend.Get(vol.Filename, basezip);
                            Compression.Compression.Decompress(basezip, basefolder);
                        }

                    RSync.RSyncDir sync;
                    using (new Logging.Timer("Parsing contents " + target))
                        sync = new Duplicati.Main.RSync.RSyncDir(target, basefolder);

                    using (new Logging.Timer("Full restore to " + target))
                        sync.Restore(target, new List<string>());

                    foreach (BackupEntry p in additions)
                    {
                        using (Core.TempFolder t = new Duplicati.Core.TempFolder())
                        {
                            using (Core.TempFile patchzip = new Duplicati.Core.TempFile())
                            {
                                using (new Logging.Timer("Get " + p.SignatureFile.Filename))
                                    backend.Get(p.SignatureFile.Filename, patchzip);
                                Compression.Compression.Decompress(patchzip, t);
                            }

                            foreach (BackupEntry vol in p.ContentVolumes)
                                using (Core.TempFile patchzip = new Duplicati.Core.TempFile())
                                {
                                    using (new Logging.Timer("Get " + vol.Filename))
                                        backend.Get(vol.Filename, patchzip);
                                    Compression.Compression.Decompress(patchzip, t);
                                }

                            using (new Logging.Timer("Incremental patch " + p.Time.ToString()))
                                sync.Patch(target, t);

                        }
                    }


                }
            }
        }

        public static string[] List(string source, Dictionary<string, string> options)
        {
            List<string> res = new List<string>();
            Duplicati.Backend.IBackendInterface i = new Duplicati.Backend.BackendLoader(source, options);
            foreach (Duplicati.Backend.FileEntry fe in i.List())
                res.Add(fe.Name);

            return res.ToArray();
        }

        public static List<BackupEntry> ParseFileList(string source, Dictionary<string, string> options)
        {
            using (new Logging.Timer("Getting and sorting filelist from " + source))
            {
                FilenameStrategy fns = new FilenameStrategy(options);

                List<BackupEntry> incrementals = new List<BackupEntry>();
                List<BackupEntry> fulls = new List<BackupEntry>();
                Dictionary<string, BackupEntry> signatures = new Dictionary<string, BackupEntry>();
                Dictionary<string, List<BackupEntry>> contents = new Dictionary<string, List<BackupEntry>>();
                string filename = "duplicati";

                Duplicati.Backend.IBackendInterface i = new Duplicati.Backend.BackendLoader(source, options);

                foreach (Duplicati.Backend.FileEntry fe in i.List())
                {
                    BackupEntry be = fns.DecodeFilename(filename, fe);
                    if (be == null)
                        continue;

                    if (be.Type == BackupEntry.EntryType.Content)
                    {
                        string content = fns.GenerateFilename(filename, BackupEntry.EntryType.Manifest, be.IsFull, be.Time) + ".manifest";
                        if (!contents.ContainsKey(content))
                            contents[content] = new List<BackupEntry>();
                        contents[content].Add(be);
                    }
                    else if (be.Type == BackupEntry.EntryType.Signature)
                    {
                        string content = fns.GenerateFilename(filename, BackupEntry.EntryType.Manifest, be.IsFull, be.Time) + ".manifest";
                        signatures[content] = be;
                    }
                    else if (be.Type != BackupEntry.EntryType.Manifest)
                        throw new Exception("Invalid entry type");
                    else if (be.IsFull)
                        fulls.Add(be);
                    else
                        incrementals.Add(be);
                }

                fulls.Sort(new Sorter());
                incrementals.Sort(new Sorter());

                foreach (BackupEntry be in fulls)
                {
                    if (contents.ContainsKey(be.Filename))
                        be.ContentVolumes.AddRange(contents[be.Filename]);
                    if (signatures.ContainsKey(be.Filename))
                        be.SignatureFile = signatures[be.Filename];
                }

                int index = 0;
                foreach (BackupEntry be in incrementals)
                {
                    if (contents.ContainsKey(be.Filename))
                        be.ContentVolumes.AddRange(contents[be.Filename]);
                    if (signatures.ContainsKey(be.Filename))
                        be.SignatureFile = signatures[be.Filename];

                    if (be.Time <= fulls[index].Time)
                    {
                        //TODO: Log this!
                        continue;
                    }
                    else
                    {
                        while (index < fulls.Count - 1 && be.Time > fulls[index].Time)
                            index++;
                        fulls[index].Incrementals.Add(be);
                    }
                }

                return fulls;
            }
        }

    }
}
