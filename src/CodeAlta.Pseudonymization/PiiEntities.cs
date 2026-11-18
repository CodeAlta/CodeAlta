using System.Collections.Frozen;

namespace CodeAlta.Pseudonymization;

/// <summary>
/// The kinds of personal data the pseudonymizer knows. Each name is also the label of the pseudonyms of its kind:
/// <c>[PERSON_001]</c>.
/// </summary>
public static class PiiEntities
{
    /// <summary>A person.</summary>
    public const string Person = "PERSON";

    /// <summary>An organization.</summary>
    public const string Organization = "ORGANIZATION";

    /// <summary>A place.</summary>
    public const string Location = "LOCATION";

    /// <summary>A postal address.</summary>
    public const string Address = "ADDRESS";

    /// <summary>An email address.</summary>
    public const string Email = "EMAIL";

    /// <summary>A phone number.</summary>
    public const string Phone = "PHONE_NUMBER";

    /// <summary>A French social security number (NIR) whose key checks out.</summary>
    public const string Nir = "NIR";

    /// <summary>What looks like a NIR, whose key does not check out, beside words that name a NIR.</summary>
    public const string NirSuspect = "NIR_SUSPECT";

    /// <summary>A French tax number (numéro fiscal, SPI) whose key checks out: it names the taxpayer.</summary>
    public const string TaxNumber = "FR_TAX_NUMBER";

    /// <summary>The reference of a French tax notice whose key checks out: it names the notice of one household.</summary>
    public const string TaxNoticeReference = "FR_TAX_NOTICE";

    /// <summary>A SIREN whose Luhn key checks out.</summary>
    public const string Siren = "SIREN";

    /// <summary>What looks like a SIREN, whose key does not check out, beside the word SIREN.</summary>
    public const string SirenSuspect = "SIREN_SUSPECT";

    /// <summary>A SIRET whose Luhn key checks out.</summary>
    public const string Siret = "SIRET";

    /// <summary>What looks like a SIRET, whose key does not check out, beside the word SIRET.</summary>
    public const string SiretSuspect = "SIRET_SUSPECT";

    /// <summary>An IBAN whose key checks out.</summary>
    public const string Iban = "IBAN";

    /// <summary>What looks like an IBAN, whose key does not check out, beside words that name a bank account.</summary>
    public const string IbanSuspect = "IBAN_SUSPECT";

    /// <summary>A payment card number whose Luhn key checks out.</summary>
    public const string CreditCard = "CREDIT_CARD";

    /// <summary>An IPv4 address.</summary>
    public const string IpAddress = "IP";

    /// <summary>A date of birth.</summary>
    public const string BirthDate = "DATE_OF_BIRTH";

    /// <summary>Any other date (of an act, a meeting, a decision): kept by default.</summary>
    public const string Date = "DATE";

    /// <summary>A French postal code followed by its town.</summary>
    public const string PostalCode = "POSTAL_CODE";

    /// <summary>A French vehicle registration plate.</summary>
    public const string Plate = "LICENSE_PLATE";

    /// <summary>The RPPS number of a health professional.</summary>
    public const string Rpps = "RPPS";

    /// <summary>A crypto wallet address (Bitcoin) whose key checks out.</summary>
    public const string Crypto = "CRYPTO";

    /// <summary>A MAC address.</summary>
    public const string MacAddress = "MAC_ADDRESS";

    /// <summary>A web address: kept by default.</summary>
    public const string Url = "URL";

    /// <summary>A UUID: kept by default, it names no one.</summary>
    public const string Uuid = "UUID";

    /// <summary>A US social security number.</summary>
    public const string UsSsn = "US_SSN";

    /// <summary>A US individual taxpayer identification number.</summary>
    public const string UsItin = "US_ITIN";

    /// <summary>A US passport number.</summary>
    public const string UsPassport = "US_PASSPORT";

    /// <summary>A US bank account number.</summary>
    public const string UsBankNumber = "US_BANK_NUMBER";

    /// <summary>A US driver license number.</summary>
    public const string UsDriverLicense = "US_DRIVER_LICENSE";

    /// <summary>An ABA routing number of a US bank.</summary>
    public const string AbaRoutingNumber = "ABA_ROUTING_NUMBER";

    /// <summary>A US National Provider Identifier of a health professional.</summary>
    public const string UsNpi = "US_NPI";

    /// <summary>A US Medicare Beneficiary Identifier.</summary>
    public const string UsMbi = "US_MBI";

    /// <summary>A US DEA certificate number of a health professional.</summary>
    public const string MedicalLicense = "MEDICAL_LICENSE";

    /// <summary>An NHS number.</summary>
    public const string UkNhs = "UK_NHS";

    /// <summary>A UK National Insurance number.</summary>
    public const string UkNino = "UK_NINO";

    /// <summary>A UK passport number.</summary>
    public const string UkPassport = "UK_PASSPORT";

    /// <summary>A UK postcode.</summary>
    public const string UkPostcode = "UK_POSTCODE";

    /// <summary>A UK driving licence number.</summary>
    public const string UkDrivingLicence = "UK_DRIVING_LICENCE";

    /// <summary>A UK vehicle registration.</summary>
    public const string UkVehicleRegistration = "UK_VEHICLE_REGISTRATION";

    /// <summary>A US employer identification number, beside its name.</summary>
    public const string Ein = "EIN";

    /// <summary>A UK company number (Companies House), beside its name.</summary>
    public const string CompanyNumber = "COMPANY_NUMBER";

    /// <summary>
    /// The kinds by priority, the surest first: when two results overlap, the one of the kind found first wins. The
    /// identifiers a key checks come before the kinds a model guesses, and an ordinary date comes last.
    /// </summary>
    public static IReadOnlyList<string> Priority { get; } =
    [
        Nir, TaxNumber, TaxNoticeReference, Siret, UsSsn, UkNino, UkNhs, UsItin, UsMbi, NirSuspect, SiretSuspect, Siren, SirenSuspect, Ein, CompanyNumber, Iban, IbanSuspect,
        AbaRoutingNumber, UsBankNumber, Rpps, UsNpi, MedicalLicense, UkPassport, UsPassport, UkDrivingLicence, UsDriverLicense, Email, Crypto,
        Phone, Address, PostalCode, UkPostcode, CreditCard, IpAddress, MacAddress, Plate, UkVehicleRegistration, BirthDate, Person, Organization,
        Location, Url, Uuid, Date,
    ];

    /// <summary>Gets every kind the pseudonymizer knows.</summary>
    public static FrozenSet<string> All { get; } = Priority.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, int> Ranks = Priority.Select(static (entity, index) => (entity, index))
        .ToFrozenDictionary(static pair => pair.entity, static pair => pair.index, StringComparer.Ordinal);

    /// <summary>Gets the rank of a kind in <see cref="Priority"/>; an unknown kind comes just before <see cref="Date"/>.</summary>
    /// <param name="entity">The kind.</param>
    /// <returns>The rank, the lowest first.</returns>
    public static int Rank(string entity) => Ranks.TryGetValue(entity, out var rank) ? rank : Priority.Count - 1;

    /// <summary>Gets whether a kind is guessed by a model, whose results can split one mention in two.</summary>
    /// <param name="entity">The kind.</param>
    /// <returns><see langword="true"/> for a person, an organization or a place.</returns>
    public static bool IsNamed(string entity) => entity is Person or Organization or Location;
}
