using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Pseudonymization.Presidio;

/// <summary>
/// The recognizers of the identifiers every country shares, ported from the generic recognizers of Presidio (MIT,
/// Microsoft; <c>predefined_recognizers/generic</c>): payment cards, crypto wallets, emails, IBAN, IP and MAC addresses,
/// URLs and UUIDs. Their patterns, scores, words of context and checks are Presidio's; the email check differs (below).
/// </summary>
public static class GlobalRecognizers
{
    // The host of a URL and its path: a name ending in a known top-level domain (url_recognizer.py).
    private const string BaseUrl = @"((www\d{0,3}[.])?[a-z0-9.\-]{1,253}[.](?:(?:com)|(?:edu)|(?:gov)|(?:int)|(?:mil)|(?:net)|(?:onl)|(?:org)|(?:pro)|(?:red)|(?:tel)|(?:uno)|(?:xxx)|(?:academy)|(?:accountant)|(?:accountants)|(?:actor)|(?:adult)|(?:africa)|(?:agency)|(?:airforce)|(?:apartments)|(?:app)|(?:archi)|(?:army)|(?:art)|(?:asia)|(?:associates)|(?:attorney)|(?:auction)|(?:audio)|(?:auto)|(?:autos)|(?:baby)|(?:band)|(?:bar)|(?:bargains)|(?:beer)|(?:berlin)|(?:best)|(?:bet)|(?:bid)|(?:bike)|(?:bio)|(?:black)|(?:blackfriday)|(?:blog)|(?:blue)|(?:boats)|(?:bond)|(?:boo)|(?:boston)|(?:bot)|(?:boutique)|(?:build)|(?:builders)|(?:business)|(?:buzz)|(?:cab)|(?:cafe)|(?:cam)|(?:camera)|(?:camp)|(?:capital)|(?:car)|(?:cards)|(?:care)|(?:careers)|(?:cars)|(?:casa)|(?:cash)|(?:casino)|(?:catering)|(?:center)|(?:ceo)|(?:cfd)|(?:charity)|(?:chat)|(?:cheap)|(?:christmas)|(?:church)|(?:city)|(?:claims)|(?:cleaning)|(?:click)|(?:clinic)|(?:clothing)|(?:cloud)|(?:club)|(?:codes)|(?:coffee)|(?:college)|(?:com)|(?:community)|(?:company)|(?:computer)|(?:condos)|(?:construction)|(?:consulting)|(?:contact)|(?:contractors)|(?:cooking)|(?:cool)|(?:coupons)|(?:courses)|(?:credit)|(?:creditcard)|(?:cricket)|(?:cruises)|(?:cyou)|(?:dad)|(?:dance)|(?:date)|(?:dating)|(?:day)|(?:degree)|(?:delivery)|(?:democrat)|(?:dental)|(?:dentist)|(?:desi)|(?:design)|(?:dev)|(?:diamonds)|(?:diet)|(?:digital)|(?:direct)|(?:directory)|(?:discount)|(?:doctor)|(?:dog)|(?:domains)|(?:download)|(?:earth)|(?:eco)|(?:education)|(?:email)|(?:energy)|(?:engineer)|(?:engineering)|(?:enterprises)|(?:equipment)|(?:esq)|(?:estate)|(?:events)|(?:exchange)|(?:expert)|(?:exposed)|(?:express)|(?:fail)|(?:faith)|(?:family)|(?:fans)|(?:farm)|(?:fashion)|(?:feedback)|(?:film)|(?:finance)|(?:financial)|(?:fish)|(?:fishing)|(?:fit)|(?:fitness)|(?:flights)|(?:florist)|(?:flowers)|(?:football)|(?:forsale)|(?:foundation)|(?:fun)|(?:fund)|(?:furniture)|(?:futbol)|(?:fyi)|(?:gallery)|(?:game)|(?:games)|(?:garden)|(?:gay)|(?:gdn)|(?:gifts)|(?:gives)|(?:giving)|(?:glass)|(?:global)|(?:gmbh)|(?:gold)|(?:golf)|(?:graphics)|(?:gratis)|(?:green)|(?:gripe)|(?:group)|(?:guide)|(?:guitars)|(?:guru)|(?:hair)|(?:hamburg)|(?:haus)|(?:health)|(?:healthcare)|(?:help)|(?:hiphop)|(?:hockey)|(?:holdings)|(?:holiday)|(?:homes)|(?:horse)|(?:hospital)|(?:host)|(?:hosting)|(?:house)|(?:how)|(?:icu)|(?:info)|(?:ink)|(?:institute)|(?:insure)|(?:international)|(?:investments)|(?:irish)|(?:jewelry)|(?:jetzt)|(?:juegos)|(?:kaufen)|(?:kids)|(?:kitchen)|(?:kiwi)|(?:krd)|(?:kyoto)|(?:land)|(?:lat)|(?:law)|(?:lawyer)|(?:lease)|(?:legal)|(?:lgbt)|(?:life)|(?:lighting)|(?:limited)|(?:limo)|(?:link)|(?:live)|(?:loan)|(?:loans)|(?:lol)|(?:london)|(?:love)|(?:ltd)|(?:ltda)|(?:luxury)|(?:maison)|(?:management)|(?:market)|(?:marketing)|(?:markets)|(?:mba)|(?:media)|(?:melbourne)|(?:meme)|(?:memorial)|(?:men)|(?:miami)|(?:mobi)|(?:moda)|(?:moe)|(?:mom)|(?:money)|(?:monster)|(?:mortgage)|(?:motorcycles)|(?:mov)|(?:movie)|(?:nagoya)|(?:name)|(?:navy)|(?:network)|(?:new)|(?:news)|(?:ngo)|(?:ninja)|(?:now)|(?:nyc)|(?:observer)|(?:okinawa)|(?:one)|(?:ong)|(?:onl)|(?:online)|(?:organic)|(?:osaka)|(?:page)|(?:paris)|(?:partners)|(?:parts)|(?:party)|(?:pet)|(?:phd)|(?:photo)|(?:photography)|(?:photos)|(?:pics)|(?:pictures)|(?:pink)|(?:pizza)|(?:place)|(?:plumbing)|(?:plus)|(?:poker)|(?:porn)|(?:press)|(?:pro)|(?:productions)|(?:prof)|(?:promo)|(?:properties)|(?:property)|(?:protection)|(?:pub)|(?:quest)|(?:racing)|(?:recipes)|(?:red)|(?:rehab)|(?:reise)|(?:reisen)|(?:rent)|(?:rentals)|(?:repair)|(?:report)|(?:republican)|(?:rest)|(?:restaurant)|(?:review)|(?:reviews)|(?:rip)|(?:rocks)|(?:rodeo)|(?:rsvp)|(?:run)|(?:saarland)|(?:sale)|(?:salon)|(?:sarl)|(?:sbs)|(?:school)|(?:schule)|(?:science)|(?:services)|(?:sex)|(?:sexy)|(?:sh)|(?:shoes)|(?:shop)|(?:shopping)|(?:show)|(?:singles)|(?:site)|(?:skin)|(?:soccer)|(?:social)|(?:software)|(?:solar)|(?:solutions)|(?:soy)|(?:space)|(?:spiegel)|(?:study)|(?:style)|(?:sucks)|(?:supply)|(?:support)|(?:surf)|(?:surgery)|(?:systems)|(?:tax)|(?:taxi)|(?:team)|(?:tech)|(?:technology)|(?:tel)|(?:theater)|(?:tips)|(?:tires)|(?:today)|(?:tools)|(?:top)|(?:tours)|(?:town)|(?:toys)|(?:trade)|(?:training)|(?:tube)|(?:uk)|(?:university)|(?:uno)|(?:vacations)|(?:ventures)|(?:vet)|(?:video)|(?:villas)|(?:vin)|(?:vip)|(?:vision)|(?:vlaanderen)|(?:vodka)|(?:vote)|(?:voting)|(?:voyage)|(?:wales)|(?:wang)|(?:watch)|(?:webcam)|(?:website)|(?:wedding)|(?:wiki)|(?:wine)|(?:work)|(?:works)|(?:world)|(?:wtf)|(?:xyz)|(?:yoga)|(?:yokohama)|(?:you)|(?:zone)|(?:ac)|(?:ad)|(?:ae)|(?:af)|(?:ag)|(?:ai)|(?:al)|(?:am)|(?:an)|(?:ao)|(?:aq)|(?:ar)|(?:as)|(?:at)|(?:au)|(?:aw)|(?:ax)|(?:az)|(?:ba)|(?:bb)|(?:bd)|(?:be)|(?:bf)|(?:bg)|(?:bh)|(?:bi)|(?:bj)|(?:bm)|(?:bn)|(?:bo)|(?:br)|(?:bs)|(?:bt)|(?:bv)|(?:bw)|(?:by)|(?:bz)|(?:ca)|(?:cc)|(?:cd)|(?:cf)|(?:cg)|(?:ch)|(?:ci)|(?:ck)|(?:cl)|(?:cm)|(?:cn)|(?:co)|(?:cr)|(?:cu)|(?:cv)|(?:cw)|(?:cx)|(?:cy)|(?:cz)|(?:de)|(?:dj)|(?:dk)|(?:dm)|(?:do)|(?:dz)|(?:ec)|(?:ee)|(?:eg)|(?:er)|(?:es)|(?:et)|(?:eu)|(?:fi)|(?:fj)|(?:fk)|(?:fm)|(?:fo)|(?:fr)|(?:ga)|(?:gb)|(?:gd)|(?:ge)|(?:gf)|(?:gg)|(?:gh)|(?:gi)|(?:gl)|(?:gm)|(?:gn)|(?:gp)|(?:gq)|(?:gr)|(?:gs)|(?:gt)|(?:gu)|(?:gw)|(?:gy)|(?:hk)|(?:hm)|(?:hn)|(?:hr)|(?:ht)|(?:hu)|(?:id)|(?:ie)|(?:il)|(?:im)|(?:in)|(?:io)|(?:iq)|(?:ir)|(?:is)|(?:it)|(?:je)|(?:jm)|(?:jo)|(?:jp)|(?:ke)|(?:kg)|(?:kh)|(?:ki)|(?:km)|(?:kn)|(?:kp)|(?:kr)|(?:kw)|(?:ky)|(?:kz)|(?:la)|(?:lb)|(?:lc)|(?:li)|(?:lk)|(?:lr)|(?:ls)|(?:lt)|(?:lu)|(?:lv)|(?:ly)|(?:ma)|(?:mc)|(?:md)|(?:me)|(?:mg)|(?:mh)|(?:mk)|(?:ml)|(?:mm)|(?:mn)|(?:mo)|(?:mp)|(?:mq)|(?:mr)|(?:ms)|(?:mt)|(?:mu)|(?:mv)|(?:mw)|(?:mx)|(?:my)|(?:mz)|(?:na)|(?:nc)|(?:ne)|(?:nf)|(?:ng)|(?:ni)|(?:nl)|(?:no)|(?:np)|(?:nr)|(?:nu)|(?:nz)|(?:om)|(?:pa)|(?:pe)|(?:pf)|(?:pg)|(?:ph)|(?:pk)|(?:pl)|(?:pm)|(?:pn)|(?:pr)|(?:ps)|(?:pt)|(?:pw)|(?:py)|(?:qa)|(?:re)|(?:ro)|(?:rs)|(?:ru)|(?:rw)|(?:sa)|(?:sb)|(?:sc)|(?:sd)|(?:se)|(?:sg)|(?:sh)|(?:si)|(?:sj)|(?:sk)|(?:sl)|(?:sm)|(?:sn)|(?:so)|(?:sr)|(?:st)|(?:su)|(?:sv)|(?:sx)|(?:sy)|(?:sz)|(?:tc)|(?:td)|(?:tf)|(?:tg)|(?:th)|(?:tj)|(?:tk)|(?:tl)|(?:tm)|(?:tn)|(?:to)|(?:tp)|(?:tr)|(?:tt)|(?:tv)|(?:tw)|(?:tz)|(?:ua)|(?:ug)|(?:uk)|(?:us)|(?:uy)|(?:uz)|(?:va)|(?:vc)|(?:ve)|(?:vg)|(?:vi)|(?:vn)|(?:vu)|(?:wf)|(?:ws)|(?:ye)|(?:yt)|(?:za)|(?:zm)|(?:zw))(?:/[^\s()<>\""']*)?)";

    /// <summary>Creates the recognizers, a new set for each analyzer.</summary>
    /// <returns>The recognizers.</returns>
    public static IReadOnlyList<PresidioPatternRecognizer> Create() =>
    [
        new(PiiEntities.CreditCard,
        [
            new("All Credit Cards (weak)", @"\b(?!1\d{12}(?!\d))((4\d{3})|(5[0-5]\d{2})|(6\d{3})|(1\d{3})|(3\d{3}))[- ]?(\d{3,4})[- ]?(\d{3,4})[- ]?(\d{3,5})\b", 0.3),
        ], ["credit", "card", "visa", "mastercard", "cc ", "amex", "discover", "jcb", "diners", "maestro", "instapayment"])
        {
            Validate = static value => PiiChecksums.IsLuhnValid(UsRecognizers.Sanitize(value, '-', ' ')),
            // Not in Presidio: one group of digits printed again and again ("4101 4101 4101 4101", the same base in each
            // column of a table) passes the Luhn key one time in ten, and no card has that number.
            Invalidate = static value => IsRepeatedGroup(UsRecognizers.Sanitize(value, '-', ' ')),
        },
        new(PiiEntities.Crypto, [new("Crypto (Medium)", @"(bc1|[13])[a-zA-HJ-NP-Z0-9]{25,59}", 0.5)], ["wallet", "btc", "bitcoin", "crypto"])
        { Validate = static value => IsBitcoinAddress(value) },
        new(PiiEntities.Email,
        [
            new("Email (Medium)", @"\b((([!#$%&'*+\-/=?^_`{|}~\w])|([!#$%&'*+\-/=?^_`{|}~\w][!#$%&'*+\-/=?^_`{|}~\.\w]{0,}[!#$%&'*+\-/=?^_`{|}~\w]))[@]\w+(?:-+\w+)*(?:\.\w+(?:-+\w+)*)+)\b", 0.5),
        ], ["email"])
        { Validate = static value => HasTopLevelDomain(value) },
        new IbanRecognizer(),
        new(PiiEntities.IpAddress,
        [
            new("IPv4_mapped", @"(?<![\w:])::(?:ffff(?::0{1,4})?:)?(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)(?:/(?:12[0-8]|1[01]\d|[1-9]?\d))?\b", 0.6),
            new("IPv4_embedded", @"(?<![\w:])(?:(?:[0-9A-Fa-f]{1,4}:){1,5}:(?:[0-9A-Fa-f]{1,4}:){0,4}|(?:[0-9A-Fa-f]{1,4}:){6})(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)(?:/(?:12[0-8]|1[01]\d|[1-9]?\d))?\b", 0.6),
            new("IPv4", @"\b(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)(?:/(?:[0-2]?\d|3[0-2]))?\b", 0.6),
            new("IPv6", @"(?<![\w:])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,7}:|:(?::[0-9A-Fa-f]{1,4}){1,7}|(?:[0-9A-Fa-f]{1,4}:){1,6}:[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,5}(?::[0-9A-Fa-f]{1,4}){1,2}|(?:[0-9A-Fa-f]{1,4}:){1,4}(?::[0-9A-Fa-f]{1,4}){1,3}|(?:[0-9A-Fa-f]{1,4}:){1,3}(?::[0-9A-Fa-f]{1,4}){1,4}|(?:[0-9A-Fa-f]{1,4}:){1,2}(?::[0-9A-Fa-f]{1,4}){1,5}|[0-9A-Fa-f]{1,4}:(?::[0-9A-Fa-f]{1,4}){1,6}|:(?::[0-9A-Fa-f]{1,4}){1,6})(?:%[0-9a-zA-Z]+)?(?:/(?:12[0-8]|1[01]\d|[1-9]?\d))?(?![\w:]|\.\d)", 0.6),
            new("IPv6_unspecified", @"(?<![\w:])::(?:/(?:12[0-8]|1[01]\d|[1-9]?\d))?(?![\w:])", 0.1),
        ], ["ip", "ipv4", "ipv6"])
        { Invalidate = static value => !IsIpInterface(value) },
        new(PiiEntities.MacAddress,
        [
            new("MAC_COLON_OR_HYPHEN", @"\b[0-9A-Fa-f]{2}([:-])(?:[0-9A-Fa-f]{2}\1){4}[0-9A-Fa-f]{2}\b", 0.6),
            new("MAC_CISCO_DOT", @"\b[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\b", 0.6),
        ], ["mac", "mac address", "hardware address", "physical address", "ethernet"])
        { Invalidate = static value => IsInvalidMac(value) },
        new(PiiEntities.Url,
        [
            new("Standard Url", "(?i)(?:https?://)" + BaseUrl, 0.6),
            new("Non schema URL", "(?i)" + BaseUrl, 0.5),
            new("Quoted URL", @"(?i)[""'](https?://" + BaseUrl + @")[""']", 0.6),
            new("Quoted Non-schema URL", @"(?i)[""'](" + BaseUrl + @")[""']", 0.5),
        ], ["url", "website", "link"]),
        new(PiiEntities.Uuid, [new("UUID (hyphenated)", @"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b", 0.5)],
            ["uuid", "guid", "unique identifier"])
        { Invalidate = static value => IsInvalidUuid(value) },
    ];

    // EmailRecognizer checks with tldextract that the domain has a suffix of the public suffix list; here its last label
    // must be letters (two or more) or punycode, which a match of the pattern without a real domain does not have.
    private static bool HasTopLevelDomain(string value)
    {
        var at = value.LastIndexOf('@');
        var dot = value.LastIndexOf('.');
        if (at < 0 || dot < at) return false;
        var label = value[(dot + 1)..];
        return label.Length >= 2 && (label.All(char.IsLetter) || label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase));
    }

    // IpRecognizer.invalidate_result: what Python's ipaddress.ip_interface does not take is dropped.
    private static bool IsIpInterface(string value)
    {
        var slash = value.IndexOf('/');
        var address = slash < 0 ? value : value[..slash];
        if (!IPAddress.TryParse(address, out var parsed)) return false;
        if (slash < 0) return true;
        var bits = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix) && prefix <= bits;
    }

    // MacAddressRecognizer.invalidate_result: twelve hex digits, not the broadcast or the zero address.
    private static bool IsInvalidMac(string value)
    {
        var cleaned = UsRecognizers.Sanitize(value, ':', '-', '.');
        return cleaned.Length != 12 || !cleaned.All(char.IsAsciiHexDigit) || cleaned.ToUpperInvariant() is "FFFFFFFFFFFF" or "000000000000";
    }

    // UuidRecognizer.invalidate_result: not the nil UUID, a version from 1 to 8, the variant of RFC 4122.
    private static bool IsInvalidUuid(string value)
    {
        if (value.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase)) return true;
        var groups = value.Split('-');
        if (groups.Length != 5 || groups.Any(static group => group.Length == 0)) return true;
        return groups[2][0] is < '1' or > '8' || char.ToLowerInvariant(groups[3][0]) is not ('8' or '9' or 'a' or 'b');
    }

    // CryptoRecognizer.validate_result: the base58 key of a P2PKH or P2SH address, or the checksum of a Bech32(m) one.
    // A number that is its first group of 1, 2, 3 or 4 digits again and again ("4101 4101 4101 4101", "1111 1111 1111 1111").
    private static bool IsRepeatedGroup(string digits)
    {
        foreach (var size in (ReadOnlySpan<int>)[1, 2, 3, 4])
        {
            if (digits.Length % size != 0 || digits.Length == size) continue;
            var repeated = true;
            for (var index = size; index < digits.Length && repeated; index++) repeated = digits[index] == digits[index % size];
            if (repeated) return true;
        }

        return false;
    }

    private static bool IsBitcoinAddress(string value)
    {
        if (value.StartsWith('1') || value.StartsWith('3')) return IsBase58Check(value);
        return value.StartsWith("bc1", StringComparison.Ordinal) && IsBech32(value);
    }

    private static bool IsBase58Check(string value)
    {
        const string digits = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var leading = value.TakeWhile(static character => character == '1').Count();
        var number = System.Numerics.BigInteger.Zero;
        foreach (var character in value[leading..])
        {
            var digit = digits.IndexOf(character, StringComparison.Ordinal);
            if (digit < 0) return false;
            number = number * 58 + digit;
        }

        var body = number.IsZero ? [] : number.ToByteArray(isUnsigned: true, isBigEndian: true);
        var bytes = new byte[leading + body.Length];
        body.CopyTo(bytes, leading);
        if (bytes.Length < 5) return false;
        var checksum = SHA256.HashData(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 4)));
        return checksum.AsSpan(0, 4).SequenceEqual(bytes.AsSpan(bytes.Length - 4));
    }

    private static bool IsBech32(string value)
    {
        const string charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
        if (value.Any(static character => character < 33 || character > 126) || value.ToLowerInvariant() != value && value.ToUpperInvariant() != value) return false;
        var bech = value.ToLowerInvariant();
        var position = bech.LastIndexOf('1');
        if (position < 1 || position + 7 > bech.Length || bech.Length > 90) return false;
        var data = new List<int>();
        foreach (var character in bech[(position + 1)..])
        {
            var index = charset.IndexOf(character, StringComparison.Ordinal);
            if (index < 0) return false;
            data.Add(index);
        }

        var hrp = bech[..position];
        List<int> values = [.. hrp.Select(static character => character >> 5), 0, .. hrp.Select(static character => character & 31), .. data];
        int[] generator = [0x3B6A57B2, 0x26508E6D, 0x1EA119FA, 0x3D4233DD, 0x2A1462B3];
        var check = 1;
        foreach (var item in values)
        {
            var top = check >> 25;
            check = (check & 0x1FFFFFF) << 5 ^ item;
            for (var bit = 0; bit < 5; bit++) if (((top >> bit) & 1) != 0) check ^= generator[bit];
        }

        return check is 1 or 0x2BC830A3;
    }
}
