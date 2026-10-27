// English source keys; translations in es, fr, de, ja, zh-CN order.
export const scriptCardMessages = {
  "Calls of the script": ["Llamadas del script", "Appels du script", "Aufrufe des Skripts", "スクリプトの呼び出し", "脚本的调用"],
  "Steps and sessions of the script": ["Pasos y sesiones del script", "Étapes et sessions du script", "Schritte und Sitzungen des Skripts", "スクリプトのステップとセッション", "脚本的步骤和会话"],
  "Script": ["Script", "Script", "Skript", "スクリプト", "脚本"],
  "Console": ["Consola", "Console", "Konsole", "コンソール", "控制台"],
  "Rerun from this step": ["Volver a ejecutar desde este paso", "Relancer à partir de cette étape", "Ab diesem Schritt erneut ausführen", "このステップから再実行", "从此步骤重新运行"],
  "Rerun with recorded results": ["Volver a ejecutar con los resultados registrados", "Relancer avec les résultats enregistrés", "Mit aufgezeichneten Ergebnissen erneut ausführen",
    "記録された結果で再実行", "使用已记录的结果重新运行"],
  "Run the script again: the steps it completed give their recorded results.": [
    "Ejecutar el script de nuevo: los pasos que completó devuelven sus resultados registrados.",
    "Relancer le script : les étapes terminées donnent leurs résultats enregistrés.",
    "Das Skript erneut ausführen: Die abgeschlossenen Schritte liefern ihre aufgezeichneten Ergebnisse.",
    "スクリプトを再実行します。完了したステップは記録された結果を返します。",
    "重新运行脚本：已完成的步骤返回其记录的结果。"],
  "Run the script again: every step it recorded gives its result, only the rest of the code runs.": [
    "Ejecutar el script de nuevo: cada paso registrado devuelve su resultado y solo se ejecuta el resto del código.",
    "Relancer le script : chaque étape enregistrée donne son résultat, seul le reste du code s’exécute.",
    "Das Skript erneut ausführen: Jeder aufgezeichnete Schritt liefert sein Ergebnis, nur der übrige Code läuft.",
    "スクリプトを再実行します。記録された各ステップは結果を返し、残りのコードだけが実行されます。",
    "重新运行脚本：每个已记录的步骤返回其结果，只运行其余代码。"],
  "Recorded result": ["Resultado registrado", "Résultat enregistré", "Aufgezeichnetes Ergebnis", "記録された結果", "已记录的结果"],
} as const;
