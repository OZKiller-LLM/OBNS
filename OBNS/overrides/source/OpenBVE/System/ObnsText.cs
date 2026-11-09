//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2026, The OpenBVE Android port contributors
//
//Redistribution and use in source and binary forms, with or without
//modification, are permitted provided that the following conditions are met:
//
//1. Redistributions of source code must retain the above copyright notice, this
//   list of conditions and the following disclaimer.
//2. Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
//THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
//ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
//WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
//DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
//ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
//(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
//LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
//ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
//(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
//SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

// OBNS: not an upstream file. OBNS's own strings in the desktop options (formMain.Obns.cs), in the 14 languages of the
// Android port's (Android/OpenBve.Android/Languages), chosen by the menu's language: its own code, else one of the same
// language (de-CH takes de-DE's, pt-PT pt-BR's), else English. Upstream's language files (assets/Languages/*.xlf) stay as
// they are.

using System.Collections.Generic;
using OpenBveApi.Interface;

namespace OpenBve
{
	internal static class ObnsText
	{
		/// <summary>A string in the menu's language.</summary>
		internal static string Get(string key)
		{
			string code = Translations.CurrentLanguageCode ?? "en-US";
			if (!Strings.TryGetValue(code, out Dictionary<string, string> strings))
			{
				string language = code.Split('-', '_')[0];
				strings = null;
				foreach (KeyValuePair<string, Dictionary<string, string>> pair in Strings)
				{
					if (pair.Key.StartsWith(language + "-"))
					{
						strings = pair.Value;
						break;
					}
				}
			}

			return strings != null && strings.TryGetValue(key, out string text) ? text : Strings["en-US"][key];
		}

		private static readonly Dictionary<string, Dictionary<string, string>> Strings = new Dictionary<string, Dictionary<string, string>>
		{
			["en-US"] = new Dictionary<string, string>
			{
				["potato_mode"] = "Potato mode",
				["potato_mode_note"] = "For slow computers: sharp transparency, textures at most 1024 px, far trains updated less often. Takes effect when a game starts.",
				["potato_mode_detail"] = "Everything for speed on slow computers, as on the Nintendo Switch: Performance transparency, textures at most 1024 px, unused textures unloaded, distant trains updated less often, and the system asked for full speed. Trees and fences get harder edges. It overrides those options while on."
			},
			["de-DE"] = new Dictionary<string, string>
			{
				["potato_mode"] = "Kartoffelmodus",
				["potato_mode_note"] = "Für langsame Computer: scharfe Transparenz, Texturen höchstens 1024 px, entfernte Züge seltener aktualisiert. Gilt ab dem nächsten Spielstart.",
				["potato_mode_detail"] = "Alles für Tempo auf langsamen Computern, wie auf der Nintendo Switch: Transparenz auf Leistung, Texturen höchstens 1024 px, ungenutzte Texturen entladen, entfernte Züge seltener aktualisiert, und das System wird um volle Leistung gebeten. Bäume und Zäune bekommen härtere Kanten. Solange er an ist, ersetzt er diese Optionen."
			},
			["es-ES"] = new Dictionary<string, string>
			{
				["potato_mode"] = "Modo patata",
				["potato_mode_note"] = "Para ordenadores lentos: transparencia nítida, texturas de 1024 px como máximo, trenes lejanos actualizados con menos frecuencia. Se aplica al iniciar una partida.",
				["potato_mode_detail"] = "Todo para la velocidad en ordenadores lentos, como en la Nintendo Switch: transparencia de rendimiento, texturas de 1024 px como máximo, texturas sin usar descargadas, trenes lejanos actualizados con menos frecuencia y el sistema con la petición de ir a máxima velocidad. Los árboles y las vallas tienen bordes más duros. Mientras está activo, sustituye esas opciones."
			},
			["fr-FR"] = new Dictionary<string, string>
			{
				["potato_mode"] = "Mode patate",
				["potato_mode_note"] = "Pour les ordinateurs lents : transparence nette, textures de 1024 px au plus, trains lointains mis à jour moins souvent. Prend effet au lancement d'une partie.",
				["potato_mode_detail"] = "Tout pour la vitesse sur les ordinateurs lents, comme sur la Nintendo Switch : transparence en mode performance, textures de 1024 px au plus, textures inutilisées déchargées, trains lointains mis à jour moins souvent, et le système invité à tourner à pleine vitesse. Les arbres et les clôtures ont des bords plus durs. Tant qu'il est activé, il remplace ces options."
			},
			["it-IT"] = new Dictionary<string, string>
			{
				["potato_mode"] = "Modalità patata",
				["potato_mode_note"] = "Per computer lenti: trasparenza netta, texture al massimo di 1024 px, treni lontani aggiornati meno spesso. Ha effetto all'avvio di una partita.",
				["potato_mode_detail"] = "Tutto per la velocità sui computer lenti, come sulla Nintendo Switch: trasparenza in modalità prestazioni, texture al massimo di 1024 px, texture inutilizzate scaricate, treni lontani aggiornati meno spesso e il sistema invitato ad andare alla massima velocità. Alberi e recinzioni hanno bordi più netti. Finché è attiva, sostituisce quelle opzioni."
			},
			["ja-JP"] = new Dictionary<string, string>
			{
				["potato_mode"] = "ポテトモード",
				["potato_mode_note"] = "低性能のパソコン向け：シャープな透過、テクスチャは最大 1024 px、遠くの列車の更新頻度を下げます。次のゲーム開始時から有効です。",
				["potato_mode_detail"] = "低性能のパソコンで速度を最優先します（Nintendo Switch と同様）：パフォーマンス優先の透過、最大 1024 px のテクスチャ、未使用のテクスチャの解放、遠くの列車の更新頻度の低下、そしてシステムに最高速度での動作を要求します。木やフェンスの縁が硬くなります。オンの間はこれらのオプションより優先されます。"
			},
			["ko-KR"] = new Dictionary<string, string>
			{
				["potato_mode"] = "감자 모드",
				["potato_mode_note"] = "저사양 컴퓨터용: 선명한 투명도, 최대 1024px 텍스처, 먼 열차의 갱신 빈도 감소. 게임을 시작할 때 적용됩니다.",
				["potato_mode_detail"] = "저사양 컴퓨터에서 속도를 최우선으로 합니다(Nintendo Switch와 같이): 성능 우선 투명도, 최대 1024px 텍스처, 사용하지 않는 텍스처 해제, 먼 열차의 갱신 빈도 감소, 그리고 시스템에 최고 속도로 실행하도록 요청합니다. 나무와 울타리의 가장자리가 더 거칠어집니다. 켜져 있는 동안 해당 옵션보다 우선합니다."
			},
			["nl-NL"] = new Dictionary<string, string>
			{
				["potato_mode"] = "Aardappelmodus",
				["potato_mode_note"] = "Voor trage computers: scherpe transparantie, texturen hooguit 1024 px, verre treinen minder vaak bijgewerkt. Geldt vanaf het starten van een spel.",
				["potato_mode_detail"] = "Alles voor snelheid op trage computers, zoals op de Nintendo Switch: transparantie op prestaties, texturen hooguit 1024 px, ongebruikte texturen vrijgegeven, verre treinen minder vaak bijgewerkt, en het systeem gevraagd op volle snelheid te draaien. Bomen en hekken krijgen hardere randen. Zolang hij aan staat, vervangt hij die opties."
			},
			["pl-PL"] = new Dictionary<string, string>
			{
				["potato_mode"] = "Tryb ziemniaczany",
				["potato_mode_note"] = "Dla wolnych komputerów: ostra przezroczystość, tekstury najwyżej 1024 px, odległe pociągi aktualizowane rzadziej. Działa od uruchomienia gry.",
				["potato_mode_detail"] = "Wszystko dla szybkości na wolnych komputerach, jak na Nintendo Switch: przezroczystość w trybie wydajności, tekstury najwyżej 1024 px, zwalnianie nieużywanych tekstur, odległe pociągi aktualizowane rzadziej oraz prośba do systemu o pełną wydajność. Drzewa i płoty mają ostrzejsze krawędzie. Gdy jest włączony, zastępuje te opcje."
			},
			["pt-BR"] = new Dictionary<string, string>
			{
				["potato_mode"] = "Modo batata",
				["potato_mode_note"] = "Para computadores lentos: transparência nítida, texturas de no máximo 1024 px, trens distantes atualizados com menos frequência. Vale ao iniciar um jogo.",
				["potato_mode_detail"] = "Tudo pela velocidade em computadores lentos, como no Nintendo Switch: transparência de desempenho, texturas de no máximo 1024 px, texturas não usadas descarregadas, trens distantes atualizados com menos frequência e o sistema solicitado a rodar em velocidade máxima. Árvores e cercas ficam com bordas mais duras. Enquanto ativo, substitui essas opções."
			},
			["ru-RU"] = new Dictionary<string, string>
			{
				["potato_mode"] = "Режим «картошка»",
				["potato_mode_note"] = "Для слабых компьютеров: резкая прозрачность, текстуры не больше 1024 пикс., дальние поезда обновляются реже. Действует с запуска игры.",
				["potato_mode_detail"] = "Всё ради скорости на слабых компьютерах, как на Nintendo Switch: прозрачность в режиме производительности, текстуры не больше 1024 пикс., выгрузка неиспользуемых текстур, дальние поезда обновляются реже, а систему просят работать на полной скорости. У деревьев и заборов края становятся резче. Пока режим включён, он заменяет эти настройки."
			},
			["zh-CN"] = new Dictionary<string, string>
			{
				["potato_mode"] = "土豆模式",
				["potato_mode_note"] = "适用于低性能电脑：锐利透明、纹理最大 1024 像素、远处列车降低更新频率。在游戏开始时生效。",
				["potato_mode_detail"] = "为低性能电脑全力提速，与 Nintendo Switch 相同：性能优先的透明、纹理最大 1024 像素、释放未使用的纹理、远处列车降低更新频率，并请求系统全速运行。树木和围栏的边缘会更硬。开启时会取代这些选项。"
			},
			["zh-HK"] = new Dictionary<string, string>
			{
				["potato_mode"] = "薯仔模式",
				["potato_mode_note"] = "適用於低效能電腦：銳利透明、紋理最大 1024 像素、遠處列車減少更新。在遊戲開始時生效。",
				["potato_mode_detail"] = "為低效能電腦全力提速，與 Nintendo Switch 相同：效能優先的透明、紋理最大 1024 像素、釋放未使用的紋理、遠處列車減少更新，並要求系統全速運行。樹木和圍欄的邊緣會較硬。開啟時會取代這些選項。"
			},
			["zh-TW"] = new Dictionary<string, string>
			{
				["potato_mode"] = "馬鈴薯模式",
				["potato_mode_note"] = "適用於低效能電腦：銳利透明、材質最大 1024 像素、遠處列車降低更新頻率。在遊戲開始時生效。",
				["potato_mode_detail"] = "為低效能電腦全力提速，與 Nintendo Switch 相同：效能優先的透明、材質最大 1024 像素、釋放未使用的材質、遠處列車降低更新頻率，並要求系統全速執行。樹木和圍欄的邊緣會較硬。開啟時會取代這些選項。"
			}
		};
	}
}
