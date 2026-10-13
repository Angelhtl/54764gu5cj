using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Terraria;
using Terraria.GameContent.Events;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace AntiLag
{
    public class Config
    {
        public int IntervaloSegundos = 300;              // 5 minutos
        public int[] AvisosSegundos = new[] { 30, 20, 10 };
        public string MensajeAviso = "[c/FFFF00:[AntiLag]] Los objetos del suelo se eliminarán en {0} segundos.";
        public string MensajeLimpieza = "[c/00FF00:[AntiLag]] Se eliminaron {0} objetos del suelo.";
        public string MensajePausa = "[c/FF8800:[AntiLag]] Evento activo: limpieza pausada.";
        public string MensajeReinicio = "[c/00FFFF:[AntiLag]] Evento terminado: el contador de limpieza se reinició.";

        public bool PausarEnEventos = true;
        public bool PausarEnBoss = true;

        public bool LimitarEnemigos = true;
        public int MaxEnemigos = 60;                      // límite de enemigos hostiles en el mundo
        public int RevisarEnemigosCadaSegundos = 5;
        public bool LimitarEnemigosDuranteEventos = false; // false = no borra enemigos en invasiones/boss
        public string MensajeEnemigos = "[c/FF5555:[AntiLag]] Se eliminaron {0} enemigos por exceder el límite.";

        public static Config Cargar(string ruta)
        {
            try
            {
                if (File.Exists(ruta))
                    return JsonConvert.DeserializeObject<Config>(File.ReadAllText(ruta)) ?? new Config();
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[AntiLag] Error leyendo config: " + ex.Message);
            }
            var c = new Config();
            File.WriteAllText(ruta, JsonConvert.SerializeObject(c, Formatting.Indented));
            return c;
        }
    }

    [ApiVersion(2, 1)]
    public class AntiLagPlugin : TerrariaPlugin
    {
        public override string Name => "AntiLag";
        public override string Author => "Claude";
        public override string Description => "Limpia items del suelo y limita enemigos. Pausa durante eventos.";
        public override Version Version => new Version(1, 0, 0);

        private Config _cfg = new Config();
        private string _ruta => Path.Combine(TShock.SavePath, "AntiLag.json");

        private int _ticks;
        private int _restante;
        private int _segEnemigos;
        private bool _estabaEnEvento;

        public AntiLagPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            _cfg = Config.Cargar(_ruta);
            _restante = _cfg.IntervaloSegundos;

            ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
            GeneralHooks.ReloadEvent += OnReload;
            Commands.ChatCommands.Add(new Command("antilag.admin", Cmd, "antilag"));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
                GeneralHooks.ReloadEvent -= OnReload;
            }
            base.Dispose(disposing);
        }

        private void OnReload(ReloadEventArgs e)
        {
            _cfg = Config.Cargar(_ruta);
            _restante = _cfg.IntervaloSegundos;
        }

        private void Cmd(CommandArgs args)
        {
            string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLower() : "";
            switch (sub)
            {
                case "now":
                    LimpiarItems();
                    _restante = _cfg.IntervaloSegundos;
                    break;
                case "reload":
                    OnReload(null);
                    args.Player.SendSuccessMessage("[AntiLag] Config recargada.");
                    break;
                default:
                    args.Player.SendInfoMessage("Uso: /antilag now | reload  (siguiente limpieza en {0}s)", _restante);
                    break;
            }
        }

        private bool HayEvento()
        {
            if (_cfg.PausarEnEventos)
            {
                if (Main.invasionType > 0) return true;
                if (Main.pumpkinMoon || Main.snowMoon) return true;
                if (DD2Event.Ongoing) return true;
                if (Main.eclipse) return true;
            }
            if (_cfg.PausarEnBoss)
            {
                for (int i = 0; i < Main.maxNPCs; i++)
                    if (Main.npc[i].active && Main.npc[i].boss) return true;
            }
            return false;
        }

        private void OnUpdate(EventArgs args)
        {
            if (++_ticks < 60) return; // 1 vez por segundo
            _ticks = 0;

            bool evento = HayEvento();

            if (evento && !_estabaEnEvento)
                TShock.Utils.Broadcast(_cfg.MensajePausa, Color.White);
            else if (!evento && _estabaEnEvento)
            {
                _restante = _cfg.IntervaloSegundos; // reinicia contador
                TShock.Utils.Broadcast(_cfg.MensajeReinicio, Color.White);
            }
            _estabaEnEvento = evento;

            // Contador de items
            if (!evento)
            {
                _restante--;
                if (_cfg.AvisosSegundos.Contains(_restante))
                    TShock.Utils.Broadcast(string.Format(_cfg.MensajeAviso, _restante), Color.White);

                if (_restante <= 0)
                {
                    LimpiarItems();
                    _restante = _cfg.IntervaloSegundos;
                }
            }

            // Límite de enemigos
            if (_cfg.LimitarEnemigos && (!evento || _cfg.LimitarEnemigosDuranteEventos))
            {
                if (++_segEnemigos >= _cfg.RevisarEnemigosCadaSegundos)
                {
                    _segEnemigos = 0;
                    LimitarEnemigos();
                }
            }
        }

        private void LimpiarItems()
        {
            int n = 0;
            for (int i = 0; i < Main.maxItems; i++)
            {
                var it = Main.item[i];
                if (it != null && it.active)
                {
                    it.active = false;
                    it.type = 0;
                    it.stack = 0;
                    NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, i);
                    n++;
                }
            }
            TShock.Utils.Broadcast(string.Format(_cfg.MensajeLimpieza, n), Color.White);
        }

        private static bool EsHostil(NPC n) =>
            n.active && !n.friendly && !n.townNPC && !n.boss && n.damage > 0 && n.catchItem == 0
            && n.type != Terraria.ID.NPCID.TargetDummy;

        private void LimitarEnemigos()
        {
            var lista = Main.npc.Where(EsHostil).ToList();
            int exceso = lista.Count - _cfg.MaxEnemigos;
            if (exceso <= 0) return;

            var jugadores = TShock.Players.Where(p => p != null && p.Active).ToList();

            // Elimina primero los más lejanos a cualquier jugador
            var victimas = lista
                .OrderByDescending(n => jugadores.Count == 0 ? 0f :
                    jugadores.Min(p => Vector2.DistanceSquared(p.TPlayer.position, n.position)))
                .Take(exceso);

            int c = 0;
            foreach (var n in victimas)
            {
                n.active = false;
                n.type = 0;
                NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, null, n.whoAmI);
                c++;
            }
            TShock.Utils.Broadcast(string.Format(_cfg.MensajeEnemigos, c), Color.White);
        }
    }
}
