// English source keys; translations in es, fr, de, ja, zh-CN order.
export const promptSurfaceMessages = {
  // The run status and the background tasks, above the editor.
  "Thinking · {elapsed}": ["Pensando · {elapsed}", "Réflexion · {elapsed}", "Denkt · {elapsed}", "思考中 · {elapsed}", "思考中 · {elapsed}"],
  "in the background": ["en segundo plano", "en arrière-plan", "im Hintergrund", "バックグラウンド", "在后台"],
  "ended": ["terminadas", "terminées", "beendet", "終了", "已结束"],
  // The `⋯` menu.
  "Compact the conversation": ["Compactar la conversación", "Compacter la conversation", "Unterhaltung komprimieren", "会話を圧縮", "压缩对话"],
  // The prompt cache countdown.
  "Prompt cache": ["Caché del prompt", "Cache du prompt", "Prompt-Cache", "プロンプトキャッシュ", "提示缓存"],
  "expired": ["caducada", "expiré", "abgelaufen", "期限切れ", "已过期"],
  "The prompt cache has expired: the next message sends the whole conversation again, at the full input price.": [
    "La caché del prompt ha caducado: el próximo mensaje envía de nuevo toda la conversación, al precio completo de entrada.",
    "Le cache du prompt a expiré : le prochain message renvoie toute la conversation, au prix plein des entrées.",
    "Der Prompt-Cache ist abgelaufen: Die nächste Nachricht sendet die ganze Unterhaltung erneut, zum vollen Eingabepreis.",
    "プロンプトキャッシュの期限が切れました。次のメッセージは会話全体を通常の入力料金で再送信します。",
    "提示缓存已过期：下一条消息会以完整输入价格重新发送整个对话。"],
  "The prompt cache expires in {time} (at {clock}). A message sent before then reads the conversation from the cache, at a fraction of the input price, and keeps it for longer.": [
    "La caché del prompt caduca en {time} (a las {clock}). Un mensaje enviado antes lee la conversación de la caché, a una fracción del precio de entrada, y la conserva más tiempo.",
    "Le cache du prompt expire dans {time} (à {clock}). Un message envoyé avant lit la conversation depuis le cache, pour une fraction du prix des entrées, et le garde plus longtemps.",
    "Der Prompt-Cache läuft in {time} ab (um {clock}). Eine Nachricht davor liest die Unterhaltung aus dem Cache, zu einem Bruchteil des Eingabepreises, und hält ihn länger.",
    "プロンプトキャッシュは {time} 後（{clock}）に期限切れになります。それまでに送るメッセージは会話をキャッシュから読み込み、入力料金の一部で済み、キャッシュも延長されます。",
    "提示缓存将在 {time} 后（{clock}）过期。在此之前发送的消息会从缓存读取对话，只需一小部分输入价格，并延长缓存时间。"],
  // The reasoning of a model whose provider lists all its efforts.
  "{model} has no reasoning effort": ["{model} no tiene esfuerzo de razonamiento", "{model} n'a pas d'effort de raisonnement", "{model} hat keinen Denkaufwand",
    "{model} には推論の強さがありません", "{model} 没有推理强度"],
  "Not used": ["No se usa", "Non utilisé", "Nicht verwendet", "使用されません", "未使用"],
  // The sub-agents at work.
  "Sub-agents at work": ["Subagentes en curso", "Sous-agents au travail", "Arbeitende Sub-Agenten", "作業中のサブエージェント", "工作中的子代理"],
  "{count} sub-agent(s) at work": ["{count} subagente(s) en curso", "{count} sous-agent(s) au travail", "{count} Sub-Agent(en) bei der Arbeit", "作業中のサブエージェント {count} 件", "{count} 个子代理工作中"],
  "{count} sub-agent(s) at work, {waiting} waiting for you": ["{count} subagente(s) en curso, {waiting} esperan tu respuesta", "{count} sous-agent(s) au travail, {waiting} vous attendent",
    "{count} Sub-Agent(en) bei der Arbeit, {waiting} warten auf dich", "作業中のサブエージェント {count} 件、{waiting} 件が応答待ち", "{count} 个子代理工作中，{waiting} 个在等你"],
  // The Workspace chip and its popover.
  "Workspace": ["Espacio de trabajo", "Espace de travail", "Arbeitsbereich", "ワークスペース", "工作区"],
  "Worktree": ["Worktree", "Worktree", "Worktree", "ワークツリー", "工作树"],
  "Works in the worktree {name} of {project}": ["Trabaja en el worktree {name} de {project}", "Travaille dans le worktree {name} de {project}",
    "Arbeitet im Worktree {name} von {project}", "{project} のワークツリー {name} で作業中", "在 {project} 的工作树 {name} 中工作"],
  "Branch": ["Rama", "Branche", "Branch", "ブランチ", "分支"],
  "Path": ["Ruta", "Chemin", "Pfad", "パス", "路径"],
  "Copy the path": ["Copiar la ruta", "Copier le chemin", "Pfad kopieren", "パスをコピー", "复制路径"],
  "Open editor": ["Abrir el editor", "Ouvrir l’éditeur", "Editor öffnen", "エディターを開く", "打开编辑器"],
  "View changes": ["Ver cambios", "Voir les modifications", "Änderungen ansehen", "変更を表示", "查看更改"],
} as const;
