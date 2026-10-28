// English source keys; translations in es, fr, de, ja, zh-CN order.
export const resumeCardMessages = {
  // The warning card of a session that waits for the reset of a limit.
  "Automatic resume": ["Reanudación automática", "Reprise automatique", "Automatische Fortsetzung", "自動再開", "自动恢复"],
  "Resumes at {time} with {provider}": ["Se reanuda a las {time} con {provider}", "Reprend à {time} avec {provider}", "Wird um {time} mit {provider} fortgesetzt", "{time} に {provider} で再開", "将于 {time} 使用 {provider} 恢复"],
  "Resumes on {date} at {time} with {provider}": ["Se reanuda el {date} a las {time} con {provider}", "Reprend le {date} à {time} avec {provider}", "Wird am {date} um {time} mit {provider} fortgesetzt", "{date} {time} に {provider} で再開", "将于 {date} {time} 使用 {provider} 恢复"],
  "Resumes at {time}": ["Se reanuda a las {time}", "Reprend à {time}", "Wird um {time} fortgesetzt", "{time} に再開", "将于 {time} 恢复"],
  "Resumes on {date} at {time}": ["Se reanuda el {date} a las {time}", "Reprend le {date} à {time}", "Wird am {date} um {time} fortgesetzt", "{date} {time} に再開", "将于 {date} {time} 恢复"],
  "Resume now": ["Reanudar ahora", "Reprendre maintenant", "Jetzt fortsetzen", "今すぐ再開", "立即恢复"],
  "The resume was not changed ({status}).": ["La reanudación no se cambió ({status}).", "La reprise n’a pas été modifiée ({status}).", "Die Fortsetzung wurde nicht geändert ({status}).", "再開は変更されませんでした ({status})。", "恢复未更改（{status}）。"],
  // Settings > Models: the resume after a limit, beside the fallback.
  "Resume when the limit resets": ["Reanudar cuando se restablezca el límite", "Reprendre quand la limite se réinitialise", "Fortsetzen, wenn das Limit zurückgesetzt wird", "制限がリセットされたら再開する", "限额重置后恢复"],
  "At most": ["Como máximo", "Au plus", "Höchstens", "最大", "最多"],
  "times in a row": ["veces seguidas", "fois de suite", "Mal hintereinander", "回連続", "次连续"],
  "Automatic resumes in a row": ["Reanudaciones automáticas seguidas", "Reprises automatiques de suite", "Automatische Fortsetzungen hintereinander", "連続する自動再開の回数", "连续自动恢复次数"],
} as const;
