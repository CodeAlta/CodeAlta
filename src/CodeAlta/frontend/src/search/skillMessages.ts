// English source keys; translations in es, fr, de, ja, zh-CN order.
export const skillSearchMessages = {
  // The hint of the search window when it offers skills after a slash.
  "commands and skills": ["comandos y habilidades", "commandes et compétences", "Befehle und Fähigkeiten", "コマンドとスキル", "命令和技能"],
  // A skill was chosen where no session can be asked to use it.
  "Open a session to use a skill.": ["Abre una sesión para usar una habilidad.", "Ouvrez une session pour utiliser une compétence.", "Öffnen Sie eine Sitzung, um eine Fähigkeit zu verwenden.",
    "スキルを使うにはセッションを開いてください。", "请打开一个会话以使用技能。"],
  // A skill was chosen while the prompt of the session could not take it (a prompt being sent, a session being loaded).
  "{name} could not be added to the prompt.": ["No se pudo añadir {name} a la instrucción.", "{name} n’a pas pu être ajouté au prompt.",
    "{name} konnte dem Prompt nicht hinzugefügt werden.", "{name} をプロンプトに追加できませんでした。", "无法将 {name} 添加到提示中。"],
  // The `/` picker of the composer.
  "Search skills…": ["Buscar habilidades…", "Rechercher des compétences…", "Fähigkeiten suchen…", "スキルを検索…", "搜索技能…"],
  "No skill matches.": ["Ninguna habilidad coincide.", "Aucune compétence ne correspond.", "Keine Fähigkeit passt.", "一致するスキルがありません。", "没有匹配的技能。"],
} as const;
