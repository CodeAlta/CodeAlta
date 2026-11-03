// The words of Settings > Secrets and of the key of a provider kept as a secret, in the five languages after English
// (es, fr, de, ja, zh-CN), merged into the dictionary of `localization.ts`. English is the key.
type Row = readonly [string, string, string, string, string];

export const secretMessages = {
  "Secrets": ["Secretos", "Secrets", "Geheimnisse", "シークレット", "机密"],
  "Add a secret…": ["Añadir un secreto…", "Ajouter un secret…", "Geheimnis hinzufügen…", "シークレットを追加…", "添加机密…"],
  "Secrets unavailable": ["Secretos no disponibles", "Secrets indisponibles", "Geheimnisse nicht verfügbar", "シークレットを利用できません", "机密不可用"],
  "Named values of this machine. The configuration refers to them as ${secret:NAME} and ${var:NAME}.": [
    "Valores con nombre de esta máquina. La configuración se refiere a ellos como ${secret:NAME} y ${var:NAME}.",
    "Valeurs nommées de cette machine. La configuration y renvoie par ${secret:NAME} et ${var:NAME}.",
    "Benannte Werte dieser Maschine. Die Konfiguration verweist mit ${secret:NAME} und ${var:NAME} darauf.",
    "このマシンの名前付きの値。設定からは ${secret:NAME} と ${var:NAME} で参照します。",
    "本机的命名值。配置以 ${secret:NAME} 和 ${var:NAME} 引用它们。"],
  "No secret on this machine.": ["Ningún secreto en esta máquina.", "Aucun secret sur cette machine.", "Kein Geheimnis auf dieser Maschine.", "このマシンにシークレットはありません。", "本机没有机密。"],
  "Set {date} by {who}": ["Definido el {date} por {who}", "Défini le {date} par {who}", "Gesetzt am {date} von {who}", "{date} に {who} が設定", "{who} 于 {date} 设置"],
  "New value of {name}": ["Nuevo valor de {name}", "Nouvelle valeur de {name}", "Neuer Wert von {name}", "{name} の新しい値", "{name} 的新值"],
  "Rotate": ["Rotar", "Renouveler", "Erneuern", "更新", "轮换"],
  "Delete this secret?": ["¿Eliminar este secreto?", "Supprimer ce secret ?", "Dieses Geheimnis löschen?", "このシークレットを削除しますか?", "删除此机密？"],
  "Delete this variable?": ["¿Eliminar esta variable?", "Supprimer cette variable ?", "Diese Variable löschen?", "この変数を削除しますか?", "删除此变量？"],
  "What refers to it stops working until it is set again.": [
    "Lo que se refiere a él deja de funcionar hasta que se defina de nuevo.",
    "Ce qui y renvoie cesse de fonctionner jusqu’à ce qu’il soit de nouveau défini.",
    "Was darauf verweist, funktioniert nicht mehr, bis es erneut gesetzt wird.",
    "再設定するまで、これを参照するものは動作しなくなります。",
    "引用它的内容将停止工作，直到重新设置。"],
  "Name of the secret": ["Nombre del secreto", "Nom du secret", "Name des Geheimnisses", "シークレットの名前", "机密名称"],
  "Name of the variable": ["Nombre de la variable", "Nom de la variable", "Name der Variable", "変数の名前", "变量名称"],
  "Value of the secret": ["Valor del secreto", "Valeur du secret", "Wert des Geheimnisses", "シークレットの値", "机密的值"],
  "Value of the variable": ["Valor de la variable", "Valeur de la variable", "Wert der Variable", "変数の値", "变量的值"],
  "Letters, digits and _, not starting with a digit.": ["Letras, dígitos y _, sin empezar por un dígito.", "Lettres, chiffres et _, sans commencer par un chiffre.", "Buchstaben, Ziffern und _, nicht mit einer Ziffer beginnend.", "英字、数字、_ のみ。数字で始めないでください。", "字母、数字和 _，不能以数字开头。"],
  "This name is already used.": ["Este nombre ya se usa.", "Ce nom est déjà utilisé.", "Dieser Name wird bereits verwendet.", "この名前はすでに使われています。", "此名称已被使用。"],
  "Keep as a secret…": ["Guardar como secreto…", "Garder comme secret…", "Als Geheimnis speichern…", "シークレットとして保存…", "保存为机密…"],
  "The key moves to a secret of this machine; the configuration file names it.": [
    "La clave pasa a un secreto de esta máquina; el archivo de configuración lo nombra.",
    "La clé passe dans un secret de cette machine ; le fichier de configuration le nomme.",
    "Der Schlüssel wird zu einem Geheimnis dieser Maschine; die Konfigurationsdatei nennt es.",
    "キーはこのマシンのシークレットに移り、設定ファイルはその名前を記します。",
    "密钥移入本机的机密；配置文件只记录其名称。"],
  "The key is the secret {name}.": ["La clave es el secreto {name}.", "La clé est le secret {name}.", "Der Schlüssel ist das Geheimnis {name}.", "キーはシークレット {name} です。", "密钥为机密 {name}。"],
  "The secret {name} is not set on this machine.": ["El secreto {name} no está definido en esta máquina.", "Le secret {name} n’est pas défini sur cette machine.", "Das Geheimnis {name} ist auf dieser Maschine nicht gesetzt.", "シークレット {name} はこのマシンに設定されていません。", "本机未设置机密 {name}。"],
} satisfies Record<string, Row>;
