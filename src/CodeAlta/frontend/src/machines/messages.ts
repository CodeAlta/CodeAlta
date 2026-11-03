// The words of the paired machines (Settings > Fleet > Machines, the machine chip of a session), in the five languages
// after English (es, fr, de, ja, zh-CN), merged into the dictionary of `localization.ts`. English is the key.
type Row = readonly [string, string, string, string, string];

export const machineMessages = {
  "Fleet": ["Flota", "Flotte", "Flotte", "フリート", "机群"],
  "Machines": ["Máquinas", "Machines", "Maschinen", "マシン", "机器"],
  "Paired machines": ["Máquinas emparejadas", "Machines associées", "Gekoppelte Maschinen", "ペアリングしたマシン", "已配对的机器"],
  "The computers this one is paired with: their sessions are listed and driven from here.": ["Los ordenadores emparejados con este: sus sesiones se listan y se manejan desde aquí.", "Les ordinateurs associés à celui-ci : leurs sessions sont listées et pilotées d’ici.", "Die mit diesem gekoppelten Computer: Ihre Sitzungen werden hier aufgelistet und gesteuert.", "このコンピューターとペアリングしたコンピューター。そのセッションをここで一覧・操作できます。", "与本机配对的计算机：在此列出并操作它们的会话。"],
  "Pair a machine…": ["Emparejar una máquina…", "Associer une machine…", "Maschine koppeln…", "マシンをペアリング…", "配对机器…"],
  "Pair a machine": ["Emparejar una máquina", "Associer une machine", "Maschine koppeln", "マシンをペアリング", "配对机器"],
  "Pairing link": ["Enlace de emparejamiento", "Lien d’association", "Kopplungslink", "ペアリングリンク", "配对链接"],
  "Paste the link that altanode pair shows on the machine.": ["Pega el enlace que muestra altanode pair en la máquina.", "Collez le lien qu’affiche altanode pair sur la machine.", "Den Link einfügen, den altanode pair auf der Maschine anzeigt.", "マシンで altanode pair が表示するリンクを貼り付けます。", "粘贴机器上 altanode pair 显示的链接。"],
  "The machine shows the same words:": ["La máquina muestra las mismas palabras:", "La machine affiche les mêmes mots :", "Die Maschine zeigt dieselben Wörter:", "マシンには同じ単語が表示されます:", "机器显示相同的词语："],
  "Pair": ["Emparejar", "Associer", "Koppeln", "ペアリング", "配对"],
  "Forget {name}?": ["¿Olvidar {name}?", "Oublier {name} ?", "{name} vergessen?", "{name} を削除しますか?", "忘记 {name}？"],
  "Forget": ["Olvidar", "Oublier", "Vergessen", "削除", "忘记"],
  "Its sessions are no longer shown here; they go on on the machine.": ["Sus sesiones dejan de mostrarse aquí; siguen en la máquina.", "Ses sessions ne sont plus affichées ici ; elles continuent sur la machine.", "Ihre Sitzungen werden hier nicht mehr angezeigt; sie laufen auf der Maschine weiter.", "そのセッションはここに表示されなくなりますが、マシン上では続きます。", "其会话不再在此显示；它们仍在该机器上继续。"],
  "Online": ["En línea", "En ligne", "Online", "オンライン", "在线"],
  "Offline": ["Sin conexión", "Hors ligne", "Offline", "オフライン", "离线"],
  "Connecting…": ["Conectando…", "Connexion…", "Verbinden…", "接続中…", "正在连接…"],
  "No paired machine.": ["Ninguna máquina emparejada.", "Aucune machine associée.", "Keine gekoppelte Maschine.", "ペアリングしたマシンはありません。", "没有已配对的机器。"],
  "Machines unavailable": ["Máquinas no disponibles", "Machines indisponibles", "Maschinen nicht verfügbar", "マシンを利用できません", "机器不可用"],
  "This is not a pairing link.": ["Esto no es un enlace de emparejamiento.", "Ce n’est pas un lien d’association.", "Das ist kein Kopplungslink.", "これはペアリングリンクではありません。", "这不是配对链接。"],
  "This pairing link has expired.": ["Este enlace de emparejamiento ha caducado.", "Ce lien d’association a expiré.", "Dieser Kopplungslink ist abgelaufen.", "このペアリングリンクは期限切れです。", "此配对链接已过期。"],
  "The machine refused this link: ask it for a new one.": ["La máquina rechazó este enlace: pídele uno nuevo.", "La machine a refusé ce lien : demandez-en un nouveau.", "Die Maschine hat diesen Link abgelehnt: Einen neuen anfordern.", "マシンがこのリンクを拒否しました。新しいリンクを発行してください。", "机器拒绝了此链接：请重新生成一个。"],
  "The machine refused this computer.": ["La máquina rechazó este ordenador.", "La machine a refusé cet ordinateur.", "Die Maschine hat diesen Computer abgelehnt.", "マシンがこのコンピューターを拒否しました。", "机器拒绝了这台计算机。"],
  "The machine does not answer.": ["La máquina no responde.", "La machine ne répond pas.", "Die Maschine antwortet nicht.", "マシンが応答しません。", "机器没有响应。"],
  "This computer is paired with it already: pairing again replaces it.": ["Este ordenador ya está emparejado con ella: emparejar de nuevo la reemplaza.", "Cet ordinateur lui est déjà associé : l’associer à nouveau la remplace.", "Dieser Computer ist bereits gekoppelt: Erneutes Koppeln ersetzt sie.", "このコンピューターはすでにペアリング済みです。再度ペアリングすると置き換えられます。", "本机已与其配对：再次配对将替换它。"],
  "Runs on {name}": ["Se ejecuta en {name}", "S’exécute sur {name}", "Läuft auf {name}", "{name} で実行", "在 {name} 上运行"],
  "{name} is offline": ["{name} está sin conexión", "{name} est hors ligne", "{name} ist offline", "{name} はオフラインです", "{name} 离线"],
} as const satisfies Record<string, Row>;
