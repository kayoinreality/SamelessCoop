// SamelessCoop Launcher for Dark Souls II: Scholar of the First Sin
// -----------------------------------------------------------------
// Standalone launcher for the Seamless Co-op mod (scheissgeist/Seamless, MIT).
// It runs the mod WITHOUT permanently modifying the game and WITHOUT ever
// overwriting your vanilla savegame.
//
//   0. CONFIG  -> interactive console: mode, IP, password, gameplay/difficulty
//   1. STAGE   -> copy dinput8.dll + ini + key + steam_appid.txt into Game\
//   2. SWAP    -> back up vanilla save, load the separate co-op save
//   3. LAUNCH  -> start DarkSoulsII.exe -offline and wait for it to close
//   4. CLEANUP -> persist co-op save, restore vanilla save, remove dinput8.dll
//
// The vanilla DS2SOFS0000.sl2 is never written by the modded game.
// Compatible with the in-box .NET Framework C# compiler (C# 5).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace SamelessCoop
{
    // Session/gameplay configuration (persisted in config.ini)
    internal sealed class Config
    {
        public string GameDir = "";
        public string Mode = "host";        // host | join
        public string HostIp = "";          // join: friend's IP
        public string Password = "";         // applied in the in-game INSERT menu
        public int MaxPlayers = 6;
        public bool AllowInvasions = false;
        public bool SyncEnemies = false;
        public bool SyncBonfires = true;
        public bool SyncItems = false;
        public int Port = 27015;
        public int ServerPort = 50031;
        public bool DebugLogging = false;
    }

    internal static class Program
    {
        static string BaseDir, ModDir, ServerDir;
        static string SavesDir, VanillaStore, SeamlessStore, BackupsDir, StateFile, ConfigFile;

        const string SaveFolderName = "DarkSoulsII";
        const int STEAM_APPID = 335300;

        static int Main(string[] args)
        {
            try
            {
                Console.Title = "SamelessCoop - DS2 Seamless Co-op Launcher";
                InitPaths();

                if (args.Length > 0 && args[0] == "--self-test") return SelfTest();

                Banner();
                StartupRepairCheck();

                string gameDir = ResolveGameDir();
                if (gameDir == null)
                {
                    Error("Nao encontrei a pasta do Dark Souls II.");
                    Console.WriteLine("  Edite " + ConfigFile + " e adicione:");
                    Console.WriteLine("    game_dir=...\\Dark Souls II Scholar of the First Sin\\Game");
                    Pause(); return 1;
                }
                Info("Jogo: " + gameDir);

                string saveDir = ResolveSaveDir();
                if (saveDir != null) Info("Save: " + saveDir);
                else Warn("Save do DS2 ainda nao existe (sera criado ao jogar).");

                // ---- top-level menu ----
                Console.WriteLine();
                Console.WriteLine("  [J] Configurar e JOGAR co-op");
                Console.WriteLine("  [R] Restaurar save vanilla (seguranca)");
                Console.WriteLine("  [Q] Sair");
                Console.WriteLine();
                Console.Write("  Opcao: ");
                string top = ReadLineLower();
                if (top == "r") { ForceRestoreVanilla(saveDir); Pause(); return 0; }
                if (top != "j" && top != "") return 0;

                // ---- CONFIG CONSOLE ----
                Config cfg = LoadConfig();
                cfg.GameDir = gameDir;
                RunConfigConsole(cfg);
                SaveConfig(cfg);

                // --dry: validate config without staging/launching (for testing)
                if (args.Any(a => a == "--dry"))
                {
                    Console.WriteLine();
                    Console.WriteLine("  === INI que seria aplicada na pasta do jogo ===");
                    Console.WriteLine(BuildIni(cfg));
                    Info("[--dry] nao vou abrir o jogo nem mexer no save.");
                    return 0;
                }

                // ---- SESSION ----
                RunSession(cfg, saveDir);
                return 0;
            }
            catch (Exception ex)
            {
                Error("Erro inesperado: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Pause(); return 1;
            }
        }

        // ===============================================================
        // CONFIG CONSOLE
        // ===============================================================
        static void RunConfigConsole(Config c)
        {
            Console.WriteLine();
            Console.WriteLine("  ===================== CONFIGURACAO =====================");
            Console.WriteLine("  (Enter mantem o valor atual mostrado entre colchetes)");
            Console.WriteLine();

            // Mode
            string modeDef = (c.Mode == "join") ? "2" : "1";
            Console.WriteLine("  Modo:  [1] HOST (hospedar)   [2] JOIN (entrar no amigo)");
            string m = AskString("Modo (1/2)", modeDef);
            c.Mode = (m == "2") ? "join" : "host";

            if (c.Mode == "join")
            {
                c.HostIp = AskString("IP do HOST (ex.: 25.x.x.x do Hamachi)", c.HostIp);
                while (c.HostIp.Length == 0)
                    c.HostIp = AskString("  -> IP nao pode ficar vazio. IP do HOST", "");
            }
            else
            {
                c.HostIp = "127.0.0.1";
            }

            // Password (applied automatically by the mod from this config; no in-game menu)
            Console.WriteLine();
            Console.WriteLine("  SENHA da sessao (host e joiner precisam usar a MESMA).");
            Console.WriteLine("  E aplicada automaticamente - voce NAO abre menu nenhum no jogo.");
            c.Password = AskString("Senha", c.Password.Length > 0 ? c.Password : "souls");
            while (c.Password.Length == 0)
                c.Password = AskString("  -> Senha nao pode ficar vazia. Senha", "souls");

            // Gameplay / difficulty
            Console.WriteLine();
            Console.WriteLine("  -------- Jogabilidade / Dificuldade --------");
            c.MaxPlayers = AskInt("Maximo de jogadores (2-6)", c.MaxPlayers, 2, 6);
            c.AllowInvasions = AskBool("Permitir invasoes PvP (mais dificil)", c.AllowInvasions);
            c.SyncEnemies = AskBool("Sincronizar inimigos (experimental)", c.SyncEnemies);
            c.SyncBonfires = AskBool("Sincronizar fogueiras", c.SyncBonfires);
            c.SyncItems = AskBool("Sincronizar itens coletados", c.SyncItems);

            // Advanced (optional)
            Console.WriteLine();
            if (AskBool("Mostrar opcoes avancadas (porta/debug)", false))
            {
                c.Port = AskInt("Porta UDP do jogo", c.Port, 1024, 65535);
                c.ServerPort = AskInt("Porta do servidor privado", c.ServerPort, 1024, 65535);
                c.DebugLogging = AskBool("Log de debug", c.DebugLogging);
            }

            // Summary
            Console.WriteLine();
            Console.WriteLine("  ===================== RESUMO =====================");
            Console.WriteLine("   Modo .............. " + (c.Mode == "host" ? "HOST" : "JOIN"));
            Console.WriteLine("   IP do host ........ " + c.HostIp);
            Console.WriteLine("   Senha ............. " + c.Password + "   (aplicada automaticamente)");
            Console.WriteLine("   Max jogadores ..... " + c.MaxPlayers);
            Console.WriteLine("   Invasoes PvP ...... " + OnOff(c.AllowInvasions));
            Console.WriteLine("   Sync inimigos ..... " + OnOff(c.SyncEnemies));
            Console.WriteLine("   Sync fogueiras .... " + OnOff(c.SyncBonfires));
            Console.WriteLine("   Sync itens ........ " + OnOff(c.SyncItems));
            Console.WriteLine("  ==================================================");
            Console.WriteLine();
            if (!AskBool("Confirmar e iniciar", true))
            {
                Info("Reabrindo configuracao...");
                RunConfigConsole(c);
            }
        }

        // ===============================================================
        // SESSION ORCHESTRATION
        // ===============================================================
        static void RunSession(Config cfg, string saveDir)
        {
            bool isHost = cfg.Mode == "host";
            Process server = null;
            bool staged = false, swapped = false;
            try
            {
                Console.WriteLine();
                Info("Preparando sessao (" + (isHost ? "HOST" : "JOIN") + ")...");

                StageMod(cfg);
                staged = true;
                Ok("Mod instalado na pasta do jogo (temporario).");

                if (isHost)
                {
                    server = StartServer();
                    if (server != null) Ok("Servidor privado iniciado (Server.exe).");
                    else Warn("Nao consegui iniciar o Server.exe.");
                }

                if (saveDir != null)
                {
                    SwapToSeamless(saveDir);
                    swapped = true;
                    Ok("Save vanilla protegido. Save de co-op carregado.");
                }

                if (Process.GetProcessesByName("steam").Length == 0)
                    Warn("A Steam nao parece aberta. Abra a Steam antes de jogar.");

                Console.WriteLine();
                Info("Abrindo Dark Souls II em modo -offline...");
                if (isHost)
                    Console.WriteLine("  Auto-HOST: a sessao abre sozinha. Compartilhe a senha: " + cfg.Password);
                else
                    Console.WriteLine("  Auto-JOIN: conectando em " + cfg.HostIp + " (senha: " + cfg.Password + ")");
                Console.WriteLine("  Sem menu no jogo. Usem a Pedra Branca para se invocar.");
                LaunchGameAndWait(cfg.GameDir);
                Console.WriteLine();
                Info("Jogo fechado. Limpando...");
            }
            finally
            {
                if (swapped && saveDir != null)
                {
                    try { SwapToVanilla(saveDir); Ok("Progresso de co-op salvo. Save vanilla restaurado."); }
                    catch (Exception ex) { Error("Falha ao restaurar save: " + ex.Message); }
                }
                if (staged)
                {
                    try { UnstageMod(cfg.GameDir); Ok("Mod removido (Steam volta a ser vanilla)."); }
                    catch (Exception ex) { Error("Falha ao remover mod: " + ex.Message); }
                }
                if (server != null)
                {
                    try { if (!server.HasExited) server.Kill(); Ok("Servidor encerrado."); }
                    catch { }
                }
            }
            Console.WriteLine();
            Ok("Sessao encerrada com seguranca.");
            Pause();
        }

        // ===============================================================
        // MOD STAGING
        // ===============================================================
        static void StageMod(Config cfg)
        {
            string dll = Path.Combine(ModDir, "dinput8.dll");
            string key = Path.Combine(ModDir, "ds2_server_public.key");
            if (!File.Exists(dll)) throw new FileNotFoundException("dinput8.dll nao encontrado em " + ModDir);

            string destDll = Path.Combine(cfg.GameDir, "dinput8.dll");
            // Preserve a pre-existing dinput8.dll only if it is NOT our mod dll.
            if (File.Exists(destDll) && !File.Exists(destDll + ".sameless_bak") && !FilesEqual(destDll, dll))
                File.Copy(destDll, destDll + ".sameless_bak", true);

            File.Copy(dll, destDll, true);
            if (File.Exists(key)) File.Copy(key, Path.Combine(cfg.GameDir, "ds2_server_public.key"), true);
            File.WriteAllText(Path.Combine(cfg.GameDir, "steam_appid.txt"), STEAM_APPID.ToString());
            File.WriteAllText(Path.Combine(cfg.GameDir, "ds2_seamless_coop.ini"), BuildIni(cfg));
        }

        static void UnstageMod(string gameDir)
        {
            TryDelete(Path.Combine(gameDir, "dinput8.dll"));
            TryDelete(Path.Combine(gameDir, "ds2_seamless_coop.ini"));
            TryDelete(Path.Combine(gameDir, "ds2_server_public.key"));
            TryDelete(Path.Combine(gameDir, "steam_appid.txt"));
            string bak = Path.Combine(gameDir, "dinput8.dll.sameless_bak");
            if (File.Exists(bak)) { File.Copy(bak, Path.Combine(gameDir, "dinput8.dll"), true); TryDelete(bak); }
        }

        static string BuildIni(Config c)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Gerado pelo SamelessCoop Launcher");
            sb.AppendLine("enabled=true");
            sb.AppendLine("debug_logging=" + Bool(c.DebugLogging));
            sb.AppendLine("max_players=" + c.MaxPlayers);
            sb.AppendLine("port=" + c.Port);
            sb.AppendLine("use_custom_server=true");
            sb.AppendLine("server_ip=" + (c.Mode == "host" ? "127.0.0.1" : c.HostIp));
            sb.AppendLine("server_port=" + c.ServerPort);
            sb.AppendLine("allow_invasions=" + Bool(c.AllowInvasions));
            sb.AppendLine("sync_bonfires=" + Bool(c.SyncBonfires));
            sb.AppendLine("sync_items=" + Bool(c.SyncItems));
            sb.AppendLine("sync_enemies=" + Bool(c.SyncEnemies));
            // SamelessCoop: config-driven session, no in-game overlay
            sb.AppendLine("auto_connect=true");
            sb.AppendLine("disable_overlay=true");
            sb.AppendLine("role=" + (c.Mode == "host" ? "host" : "join"));
            sb.AppendLine("password=" + c.Password);
            return sb.ToString();
        }

        // ===============================================================
        // CONFIG PERSISTENCE (config.ini in the launcher folder)
        // ===============================================================
        static Config LoadConfig()
        {
            var c = new Config();
            if (!File.Exists(ConfigFile)) return c;
            foreach (var raw in File.ReadAllLines(ConfigFile))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "game_dir": c.GameDir = v; break;
                    case "mode": c.Mode = v.ToLowerInvariant() == "join" ? "join" : "host"; break;
                    case "host_ip": c.HostIp = v; break;
                    case "password": c.Password = v; break;
                    case "max_players": c.MaxPlayers = ParseInt(v, c.MaxPlayers); break;
                    case "allow_invasions": c.AllowInvasions = IsTrue(v); break;
                    case "sync_enemies": c.SyncEnemies = IsTrue(v); break;
                    case "sync_bonfires": c.SyncBonfires = IsTrue(v); break;
                    case "sync_items": c.SyncItems = IsTrue(v); break;
                    case "port": c.Port = ParseInt(v, c.Port); break;
                    case "server_port": c.ServerPort = ParseInt(v, c.ServerPort); break;
                    case "debug_logging": c.DebugLogging = IsTrue(v); break;
                }
            }
            return c;
        }

        static void SaveConfig(Config c)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# SamelessCoop - ultimas configuracoes");
            if (c.GameDir.Length > 0) sb.AppendLine("game_dir=" + c.GameDir);
            sb.AppendLine("mode=" + c.Mode);
            sb.AppendLine("host_ip=" + c.HostIp);
            sb.AppendLine("password=" + c.Password);
            sb.AppendLine("max_players=" + c.MaxPlayers);
            sb.AppendLine("allow_invasions=" + Bool(c.AllowInvasions));
            sb.AppendLine("sync_enemies=" + Bool(c.SyncEnemies));
            sb.AppendLine("sync_bonfires=" + Bool(c.SyncBonfires));
            sb.AppendLine("sync_items=" + Bool(c.SyncItems));
            sb.AppendLine("port=" + c.Port);
            sb.AppendLine("server_port=" + c.ServerPort);
            sb.AppendLine("debug_logging=" + Bool(c.DebugLogging));
            File.WriteAllText(ConfigFile, sb.ToString());
        }

        // ===============================================================
        // SAVE SWAPPING (the safety guarantee) -- unchanged, self-tested
        // ===============================================================
        static void SwapToSeamless(string saveDir)
        {
            BackupLive(saveDir, "pre_seamless");
            if (ReadState() == "seamless")
            {
                Warn("Sessao anterior nao fechou direito; mantendo o save de co-op atual.");
                return;
            }
            MirrorSaves(saveDir, VanillaStore);
            if (HasSaves(SeamlessStore))
            {
                LoadSaves(SeamlessStore, saveDir);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("  Primeira sessao de co-op:");
                Console.WriteLine("    [1] Personagem NOVO (recomendado)");
                Console.WriteLine("    [2] CLONAR meu personagem vanilla atual");
                string c = AskString("Opcao", "1");
                if (c == "2") Info("Clonando save vanilla para o co-op.");
                else { ClearSaves(saveDir); Info("Save de co-op vazio: o jogo criara um personagem novo."); }
            }
            WriteState("seamless");
        }

        static void SwapToVanilla(string saveDir)
        {
            if (ReadState() != "seamless") return;
            MirrorSaves(saveDir, SeamlessStore);
            if (HasSaves(VanillaStore)) LoadSaves(VanillaStore, saveDir);
            else ClearSaves(saveDir);
            BackupLive(saveDir, "post_restore");
            WriteState("vanilla");
        }

        static void ForceRestoreVanilla(string saveDir)
        {
            if (saveDir == null) { Warn("Sem pasta de save."); return; }
            Info("Restaurando save vanilla a partir de saves\\vanilla ...");
            BackupLive(saveDir, "manual_pre");
            if (HasSaves(VanillaStore)) { LoadSaves(VanillaStore, saveDir); WriteState("vanilla"); Ok("Save vanilla restaurado."); }
            else Warn("Nao ha save vanilla guardado em saves\\vanilla.");
        }

        static void StartupRepairCheck()
        {
            try
            {
                if (ReadState() == "seamless")
                {
                    Console.WriteLine();
                    Warn("A sessao anterior nao foi encerrada corretamente.");
                    Warn("A pasta de save pode conter o save de CO-OP, nao o vanilla.");
                    if (AskBool("Restaurar o save vanilla agora", true))
                    {
                        string saveDir = ResolveSaveDir();
                        if (saveDir != null)
                        {
                            MirrorSaves(saveDir, SeamlessStore);
                            if (HasSaves(VanillaStore)) LoadSaves(VanillaStore, saveDir);
                            WriteState("vanilla");
                            Ok("Save vanilla restaurado. Tudo certo.");
                        }
                    }
                }
            }
            catch (Exception ex) { Warn("Repair check: " + ex.Message); }
        }

        static IEnumerable<string> Sl2Files(string dir)
        {
            if (!Directory.Exists(dir)) return new string[0];
            return Directory.GetFiles(dir, "DS2SOFS*.sl2");
        }
        static bool HasSaves(string dir) { return Sl2Files(dir).Any(); }
        static void MirrorSaves(string fromDir, string toStore)
        {
            Directory.CreateDirectory(toStore);
            foreach (var f in Sl2Files(toStore)) TryDelete(f);
            foreach (var f in Sl2Files(fromDir)) File.Copy(f, Path.Combine(toStore, Path.GetFileName(f)), true);
        }
        static void LoadSaves(string fromStore, string toLive)
        {
            Directory.CreateDirectory(toLive);
            foreach (var f in Sl2Files(toLive)) TryDelete(f);
            foreach (var f in Sl2Files(fromStore)) File.Copy(f, Path.Combine(toLive, Path.GetFileName(f)), true);
        }
        static void ClearSaves(string liveDir) { foreach (var f in Sl2Files(liveDir)) TryDelete(f); }
        static void BackupLive(string saveDir, string tag)
        {
            if (saveDir == null || !HasSaves(saveDir)) return;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dst = Path.Combine(BackupsDir, stamp + "_" + tag);
            Directory.CreateDirectory(dst);
            foreach (var f in Sl2Files(saveDir)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        }
        static string ReadState()
        {
            try { return File.Exists(StateFile) ? File.ReadAllText(StateFile).Trim() : "vanilla"; }
            catch { return "vanilla"; }
        }
        static void WriteState(string s) { File.WriteAllText(StateFile, s); }

        // ===============================================================
        // LAUNCH
        // ===============================================================
        static void LaunchGameAndWait(string gameDir)
        {
            string exe = Path.Combine(gameDir, "DarkSoulsII.exe");
            var psi = new ProcessStartInfo(exe, "-offline");
            psi.WorkingDirectory = gameDir;
            psi.UseShellExecute = false;
            Process p = Process.Start(psi);

            bool appeared = false;
            for (int i = 0; i < 60; i++)
            {
                if (Process.GetProcessesByName("DarkSoulsII").Length > 0) { appeared = true; break; }
                Thread.Sleep(1000);
            }
            if (!appeared) Warn("O jogo nao apareceu. A Steam esta aberta? Esperando mesmo assim...");

            int gone = 0;
            while (gone < 3)
            {
                Thread.Sleep(3000);
                if (Process.GetProcessesByName("DarkSoulsII").Length == 0) gone++; else gone = 0;
            }
            try { if (p != null && !p.HasExited) p.WaitForExit(2000); } catch { }
        }

        static Process StartServer()
        {
            string exe = Path.Combine(ServerDir, "Server.exe");
            if (!File.Exists(exe)) return null;
            var psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = ServerDir;
            psi.UseShellExecute = true;
            try { return Process.Start(psi); } catch { return null; }
        }

        // ===============================================================
        // PATH RESOLUTION
        // ===============================================================
        static void InitPaths()
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            ModDir = Path.Combine(BaseDir, "mod");
            ServerDir = Path.Combine(ModDir, "Server");
            SavesDir = Path.Combine(BaseDir, "saves");
            VanillaStore = Path.Combine(SavesDir, "vanilla");
            SeamlessStore = Path.Combine(SavesDir, "seamless");
            BackupsDir = Path.Combine(SavesDir, "backups");
            StateFile = Path.Combine(SavesDir, "state.txt");
            ConfigFile = Path.Combine(BaseDir, "config.ini");
            Directory.CreateDirectory(VanillaStore);
            Directory.CreateDirectory(SeamlessStore);
            Directory.CreateDirectory(BackupsDir);
        }

        static string ResolveGameDir()
        {
            if (File.Exists(ConfigFile))
            {
                foreach (var line in File.ReadAllLines(ConfigFile))
                {
                    var t = line.Trim();
                    if (t.StartsWith("game_dir=", StringComparison.OrdinalIgnoreCase))
                    {
                        string p = t.Substring("game_dir=".Length).Trim();
                        if (p.Length > 0 && File.Exists(Path.Combine(p, "DarkSoulsII.exe"))) return p;
                    }
                }
            }
            string rel = "steamapps\\common\\Dark Souls II Scholar of the First Sin\\Game";
            string[] roots = {
                "C:\\Program Files (x86)\\Steam", "C:\\Program Files\\Steam", "C:\\SteamLibrary",
                "D:\\Steam", "D:\\SteamLibrary", "E:\\Steam", "E:\\SteamLibrary",
                "F:\\Steam", "F:\\SteamLibrary", "G:\\Steam", "G:\\SteamLibrary"
            };
            foreach (var r in roots)
            {
                string g = Path.Combine(r, rel);
                if (File.Exists(Path.Combine(g, "DarkSoulsII.exe"))) return g;
            }
            return null;
        }

        static string ResolveSaveDir()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string ds2 = Path.Combine(appData, SaveFolderName);
            if (!Directory.Exists(ds2)) return null;
            foreach (var d in Directory.GetDirectories(ds2))
                if (Directory.GetFiles(d, "DS2SOFS*.sl2").Length > 0) return d;
            var subs = Directory.GetDirectories(ds2);
            return subs.Length > 0 ? subs[0] : null;
        }

        // ===============================================================
        // SELF-TEST (sandbox only; touches NO real files)
        // ===============================================================
        static int SelfTest()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "sameless_selftest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string live = Path.Combine(sandbox, "live");
            Directory.CreateDirectory(live);
            SavesDir = Path.Combine(sandbox, "saves");
            VanillaStore = Path.Combine(SavesDir, "vanilla");
            SeamlessStore = Path.Combine(SavesDir, "seamless");
            BackupsDir = Path.Combine(SavesDir, "backups");
            StateFile = Path.Combine(SavesDir, "state.txt");
            Directory.CreateDirectory(VanillaStore);
            Directory.CreateDirectory(SeamlessStore);
            Directory.CreateDirectory(BackupsDir);
            WriteState("vanilla");

            string liveSave = Path.Combine(live, "DS2SOFS0000.sl2");
            string VANILLA = "VANILLA", COOP1 = "COOP1", COOP2 = "COOP2";
            int fails = 0;
            File.WriteAllText(liveSave, VANILLA);
            Console.WriteLine("[selftest] inicio: live=vanilla");

            // Session 1 (fresh)
            BackupLive(live, "pre_seamless"); MirrorSaves(live, VanillaStore); ClearSaves(live); WriteState("seamless");
            fails += Expect(!File.Exists(liveSave), "apos swap->seamless (fresh), live sem sl2");
            fails += Expect(File.ReadAllText(Path.Combine(VanillaStore, "DS2SOFS0000.sl2")) == VANILLA, "vanilla guardado");
            File.WriteAllText(liveSave, COOP1); SwapToVanilla(live);
            fails += Expect(File.ReadAllText(liveSave) == VANILLA, "apos restore, live=vanilla");
            fails += Expect(File.ReadAllText(Path.Combine(SeamlessStore, "DS2SOFS0000.sl2")) == COOP1, "co-op 1 persistido");

            // Session 2 (loads co-op)
            SwapToSeamlessNoPrompt(live);
            fails += Expect(File.ReadAllText(liveSave) == COOP1, "sessao 2 carrega co-op");
            File.WriteAllText(liveSave, COOP2); SwapToVanilla(live);
            fails += Expect(File.ReadAllText(liveSave) == VANILLA, "vanilla intacto apos sessao 2");
            fails += Expect(File.ReadAllText(Path.Combine(SeamlessStore, "DS2SOFS0000.sl2")) == COOP2, "co-op 2 persistido");

            // Crash recovery
            SwapToSeamlessNoPrompt(live); File.WriteAllText(liveSave, "MID");
            fails += Expect(ReadState() == "seamless", "estado=seamless apos crash");
            MirrorSaves(live, SeamlessStore); LoadSaves(VanillaStore, live); WriteState("vanilla");
            fails += Expect(File.ReadAllText(liveSave) == VANILLA, "recuperacao restaura vanilla");
            fails += Expect(Directory.GetDirectories(BackupsDir).Length > 0, "backups criados");

            try { Directory.Delete(sandbox, true); } catch { }
            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "[selftest] TODOS OS TESTES PASSARAM [OK]" : "[selftest] " + fails + " FALHARAM");
            return fails == 0 ? 0 : 1;
        }

        // seamless swap without console prompt (assumes existing co-op save)
        static void SwapToSeamlessNoPrompt(string saveDir)
        {
            BackupLive(saveDir, "pre_seamless");
            if (ReadState() == "seamless") return;
            MirrorSaves(saveDir, VanillaStore);
            if (HasSaves(SeamlessStore)) LoadSaves(SeamlessStore, saveDir); else ClearSaves(saveDir);
            WriteState("seamless");
        }

        static int Expect(bool cond, string label)
        {
            Console.WriteLine((cond ? "  [PASS] " : "  [FAIL] ") + label);
            return cond ? 0 : 1;
        }

        // ===============================================================
        // small utils / console helpers
        // ===============================================================
        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        static bool FilesEqual(string a, string b)
        {
            try
            {
                var fa = new FileInfo(a); var fb = new FileInfo(b);
                if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
                using (var sa = fa.OpenRead())
                using (var sb = fb.OpenRead())
                {
                    int x, y;
                    do { x = sa.ReadByte(); y = sb.ReadByte(); if (x != y) return false; } while (x != -1);
                }
                return true;
            }
            catch { return false; }
        }

        static string AskString(string label, string def)
        {
            Console.Write("  " + label + " [" + def + "]: ");
            string s = Console.ReadLine();
            if (s == null) return def;
            s = s.Trim();
            return s.Length == 0 ? def : s;
        }
        static bool AskBool(string label, bool def)
        {
            string d = def ? "S/n" : "s/N";
            Console.Write("  " + label + " [" + d + "]: ");
            string s = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return def;
            return s == "s" || s == "y" || s == "sim" || s == "1";
        }
        static int AskInt(string label, int def, int min, int max)
        {
            while (true)
            {
                Console.Write("  " + label + " [" + def + "]: ");
                string s = (Console.ReadLine() ?? "").Trim();
                if (s.Length == 0) return def;
                int v;
                if (int.TryParse(s, out v) && v >= min && v <= max) return v;
                Console.WriteLine("    valor invalido (entre " + min + " e " + max + ")");
            }
        }
        static string ReadLineLower() { return (Console.ReadLine() ?? "").Trim().ToLowerInvariant(); }

        static int ParseInt(string s, int def) { int v; return int.TryParse(s, out v) ? v : def; }
        static bool IsTrue(string v) { v = v.ToLowerInvariant(); return v == "true" || v == "1" || v == "s" || v == "yes"; }
        static string Bool(bool b) { return b ? "true" : "false"; }
        static string OnOff(bool b) { return b ? "LIGADO" : "desligado"; }

        static void Banner()
        {
            Console.WriteLine();
            Console.WriteLine("  ===============================================");
            Console.WriteLine("   SAMELESSCOOP - Dark Souls II Seamless Co-op");
            Console.WriteLine("   Launcher + Config (save vanilla protegido)");
            Console.WriteLine("  ===============================================");
        }
        static void Info(string s) { Console.WriteLine("  [>] " + s); }
        static void Ok(string s) { Console.WriteLine("  [OK] " + s); }
        static void Warn(string s) { Console.WriteLine("  [!] " + s); }
        static void Error(string s) { Console.WriteLine("  [ERRO] " + s); }
        static void Pause() { Console.WriteLine(); Console.Write("  Pressione Enter para sair..."); Console.ReadLine(); }
    }
}
