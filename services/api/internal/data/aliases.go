// Package data, aliases.go: name resolution for sports teams.
//
// Upstream feeds (e.g. streamed.pk) hand us free-text team names like
// "Manchester United" or "Mumbai Indians". The rest of the API speaks
// canonical kebab-case IDs ("man-united", "mumbai-indians"). This file
// owns that translation.
//
// Resolution is two-tiered:
//
//  1. An in-process alias map keyed on a normalized form of the input.
//     Normalization lowercases, strips common suffixes ("FC", "F.C."),
//     drops punctuation, and collapses whitespace. The map carries
//     several aliases per canonical id (full name, short form, common
//     abbreviation) so we hit on the natural inputs a feed will send.
//
//  2. If no alias matches, and an OpenAI source is wired, the lookup
//     falls back to asking the LLM. That path is opt-in via
//     OPENAI_API_KEY at the call site (see main.go).
//
// The alias table is intentionally small and curated rather than
// exhaustive — the OpenAI fallback covers the long tail.
package data

import (
	"context"
	"regexp"
	"strings"

	"github.com/abindlish/streamed-backend/internal/api"
)

// teamMeta is the canonical record an alias resolves to. Kept private
// to this package; the public API speaks api.TeamLookup.
type teamMeta struct {
	ID            string
	CanonicalName string
	League        string
}

// teamCatalog enumerates every canonical team the alias table can map
// onto, plus the league and display name we surface to the TV client.
// Add new clubs here, then add their aliases in teamAliases below.
var teamCatalog = map[string]teamMeta{
	// Premier League — big six + common mid-table.
	"arsenal":          {ID: "arsenal", CanonicalName: "Arsenal", League: "Premier League"},
	"man-city":         {ID: "man-city", CanonicalName: "Manchester City", League: "Premier League"},
	"man-united":       {ID: "man-united", CanonicalName: "Manchester United", League: "Premier League"},
	"liverpool":        {ID: "liverpool", CanonicalName: "Liverpool", League: "Premier League"},
	"chelsea":          {ID: "chelsea", CanonicalName: "Chelsea", League: "Premier League"},
	"spurs":            {ID: "spurs", CanonicalName: "Tottenham Hotspur", League: "Premier League"},
	"newcastle":        {ID: "newcastle", CanonicalName: "Newcastle United", League: "Premier League"},
	"aston-villa":      {ID: "aston-villa", CanonicalName: "Aston Villa", League: "Premier League"},
	"west-ham":         {ID: "west-ham", CanonicalName: "West Ham United", League: "Premier League"},
	"brighton":         {ID: "brighton", CanonicalName: "Brighton & Hove Albion", League: "Premier League"},
	"everton":          {ID: "everton", CanonicalName: "Everton", League: "Premier League"},
	"wolves":           {ID: "wolves", CanonicalName: "Wolverhampton Wanderers", League: "Premier League"},
	"fulham":           {ID: "fulham", CanonicalName: "Fulham", League: "Premier League"},
	"brentford":        {ID: "brentford", CanonicalName: "Brentford", League: "Premier League"},
	"nottm-forest":     {ID: "nottm-forest", CanonicalName: "Nottingham Forest", League: "Premier League"},
	"leicester":        {ID: "leicester", CanonicalName: "Leicester City", League: "Premier League"},

	// Championship.
	"leeds":            {ID: "leeds", CanonicalName: "Leeds United", League: "Championship"},
	"burnley":          {ID: "burnley", CanonicalName: "Burnley", League: "Championship"},
	"sunderland":       {ID: "sunderland", CanonicalName: "Sunderland", League: "Championship"},

	// La Liga.
	"real-madrid":      {ID: "real-madrid", CanonicalName: "Real Madrid", League: "La Liga"},
	"barcelona":        {ID: "barcelona", CanonicalName: "Barcelona", League: "La Liga"},
	"atletico-madrid":  {ID: "atletico-madrid", CanonicalName: "Atlético Madrid", League: "La Liga"},
	"sevilla":          {ID: "sevilla", CanonicalName: "Sevilla", League: "La Liga"},
	"valencia":         {ID: "valencia", CanonicalName: "Valencia", League: "La Liga"},
	"villarreal":       {ID: "villarreal", CanonicalName: "Villarreal", League: "La Liga"},
	"real-sociedad":    {ID: "real-sociedad", CanonicalName: "Real Sociedad", League: "La Liga"},
	"athletic-bilbao":  {ID: "athletic-bilbao", CanonicalName: "Athletic Bilbao", League: "La Liga"},
	"girona":           {ID: "girona", CanonicalName: "Girona", League: "La Liga"},
	"rayo-vallecano":   {ID: "rayo-vallecano", CanonicalName: "Rayo Vallecano", League: "La Liga"},
	"betis":            {ID: "betis", CanonicalName: "Real Betis", League: "La Liga"},
	"celta-vigo":       {ID: "celta-vigo", CanonicalName: "Celta Vigo", League: "La Liga"},
	"osasuna":          {ID: "osasuna", CanonicalName: "Osasuna", League: "La Liga"},
	"getafe":           {ID: "getafe", CanonicalName: "Getafe", League: "La Liga"},

	// Bundesliga.
	"bayern":           {ID: "bayern", CanonicalName: "Bayern Munich", League: "Bundesliga"},
	"dortmund":         {ID: "dortmund", CanonicalName: "Borussia Dortmund", League: "Bundesliga"},
	"rb-leipzig":       {ID: "rb-leipzig", CanonicalName: "RB Leipzig", League: "Bundesliga"},
	"leverkusen":       {ID: "leverkusen", CanonicalName: "Bayer Leverkusen", League: "Bundesliga"},
	"frankfurt":        {ID: "frankfurt", CanonicalName: "Eintracht Frankfurt", League: "Bundesliga"},

	// Serie A.
	"juventus":         {ID: "juventus", CanonicalName: "Juventus", League: "Serie A"},
	"ac-milan":         {ID: "ac-milan", CanonicalName: "AC Milan", League: "Serie A"},
	"inter-milan":      {ID: "inter-milan", CanonicalName: "Inter Milan", League: "Serie A"},
	"napoli":           {ID: "napoli", CanonicalName: "Napoli", League: "Serie A"},
	"roma":             {ID: "roma", CanonicalName: "AS Roma", League: "Serie A"},
	"lazio":            {ID: "lazio", CanonicalName: "Lazio", League: "Serie A"},

	// Ligue 1.
	"psg":              {ID: "psg", CanonicalName: "Paris Saint-Germain", League: "Ligue 1"},
	"marseille":        {ID: "marseille", CanonicalName: "Olympique de Marseille", League: "Ligue 1"},
	"lyon":             {ID: "lyon", CanonicalName: "Olympique Lyonnais", League: "Ligue 1"},
	"monaco":           {ID: "monaco", CanonicalName: "AS Monaco", League: "Ligue 1"},

	// NBA.
	"cleveland-cavaliers": {ID: "cleveland-cavaliers", CanonicalName: "Cleveland Cavaliers", League: "NBA"},
	"la-lakers":           {ID: "la-lakers", CanonicalName: "Los Angeles Lakers", League: "NBA"},
	"boston-celtics":      {ID: "boston-celtics", CanonicalName: "Boston Celtics", League: "NBA"},
	"golden-state":        {ID: "golden-state", CanonicalName: "Golden State Warriors", League: "NBA"},
	"chicago-bulls":       {ID: "chicago-bulls", CanonicalName: "Chicago Bulls", League: "NBA"},
	"miami-heat":          {ID: "miami-heat", CanonicalName: "Miami Heat", League: "NBA"},
	"new-york-knicks":     {ID: "new-york-knicks", CanonicalName: "New York Knicks", League: "NBA"},
	"milwaukee-bucks":     {ID: "milwaukee-bucks", CanonicalName: "Milwaukee Bucks", League: "NBA"},
	"denver-nuggets":      {ID: "denver-nuggets", CanonicalName: "Denver Nuggets", League: "NBA"},
	"phoenix-suns":        {ID: "phoenix-suns", CanonicalName: "Phoenix Suns", League: "NBA"},
	"okc-thunder":         {ID: "okc-thunder", CanonicalName: "Oklahoma City Thunder", League: "NBA"},
	"indiana-pacers":      {ID: "indiana-pacers", CanonicalName: "Indiana Pacers", League: "NBA"},
	"minnesota-timberwolves": {ID: "minnesota-timberwolves", CanonicalName: "Minnesota Timberwolves", League: "NBA"},
	"san-antonio-spurs":   {ID: "san-antonio-spurs", CanonicalName: "San Antonio Spurs", League: "NBA"},
	"dallas-mavericks":    {ID: "dallas-mavericks", CanonicalName: "Dallas Mavericks", League: "NBA"},
	"philadelphia-76ers":  {ID: "philadelphia-76ers", CanonicalName: "Philadelphia 76ers", League: "NBA"},
	"brooklyn-nets":       {ID: "brooklyn-nets", CanonicalName: "Brooklyn Nets", League: "NBA"},
	"toronto-raptors":     {ID: "toronto-raptors", CanonicalName: "Toronto Raptors", League: "NBA"},
	"la-clippers":         {ID: "la-clippers", CanonicalName: "LA Clippers", League: "NBA"},
	"new-orleans-pelicans": {ID: "new-orleans-pelicans", CanonicalName: "New Orleans Pelicans", League: "NBA"},
	"memphis-grizzlies":   {ID: "memphis-grizzlies", CanonicalName: "Memphis Grizzlies", League: "NBA"},

	// IPL — top-tier franchises.
	"mumbai-indians":              {ID: "mumbai-indians", CanonicalName: "Mumbai Indians", League: "IPL"},
	"chennai-super-kings":         {ID: "chennai-super-kings", CanonicalName: "Chennai Super Kings", League: "IPL"},
	"royal-challengers-bangalore": {ID: "royal-challengers-bangalore", CanonicalName: "Royal Challengers Bengaluru", League: "IPL"},
	"kolkata-knight-riders":       {ID: "kolkata-knight-riders", CanonicalName: "Kolkata Knight Riders", League: "IPL"},
	"delhi-capitals":              {ID: "delhi-capitals", CanonicalName: "Delhi Capitals", League: "IPL"},
	"punjab-kings":                {ID: "punjab-kings", CanonicalName: "Punjab Kings", League: "IPL"},
	"rajasthan-royals":            {ID: "rajasthan-royals", CanonicalName: "Rajasthan Royals", League: "IPL"},
	"sunrisers-hyderabad":         {ID: "sunrisers-hyderabad", CanonicalName: "Sunrisers Hyderabad", League: "IPL"},
	"lucknow-super-giants":        {ID: "lucknow-super-giants", CanonicalName: "Lucknow Super Giants", League: "IPL"},
	"gujarat-titans":              {ID: "gujarat-titans", CanonicalName: "Gujarat Titans", League: "IPL"},

	// International cricket sides.
	"india":       {ID: "india", CanonicalName: "India", League: "International Cricket"},
	"australia":   {ID: "australia", CanonicalName: "Australia", League: "International Cricket"},
	"england":     {ID: "england", CanonicalName: "England", League: "International Cricket"},
	"pakistan":    {ID: "pakistan", CanonicalName: "Pakistan", League: "International Cricket"},
	"new-zealand": {ID: "new-zealand", CanonicalName: "New Zealand", League: "International Cricket"},
	"south-africa": {ID: "south-africa", CanonicalName: "South Africa", League: "International Cricket"},
	"west-indies": {ID: "west-indies", CanonicalName: "West Indies", League: "International Cricket"},
	"sri-lanka":   {ID: "sri-lanka", CanonicalName: "Sri Lanka", League: "International Cricket"},
	"bangladesh":  {ID: "bangladesh", CanonicalName: "Bangladesh", League: "International Cricket"},
}

// teamAliases maps a normalized free-text name to a canonical key in
// teamCatalog. Several entries per team is encouraged — feeds vary.
//
// Keys MUST be the output of normalizeTeamName so lookups stay O(1).
var teamAliases = map[string]string{
	// Arsenal.
	"arsenal":        "arsenal",
	"arsenal london": "arsenal",
	"the gunners":   "arsenal",
	"gunners":       "arsenal",

	// Manchester City.
	"manchester city": "man-city",
	"man city":       "man-city",
	"mancity":        "man-city",
	"city":           "man-city",

	// Manchester United.
	"manchester united": "man-united",
	"man united":       "man-united",
	"man utd":          "man-united",
	"manu":             "man-united",
	"red devils":       "man-united",

	// Liverpool.
	"liverpool": "liverpool",
	"the reds":  "liverpool",
	"lfc":       "liverpool",

	// Chelsea.
	"chelsea":   "chelsea",
	"the blues": "chelsea",
	"cfc":       "chelsea",

	// Tottenham.
	"tottenham":         "spurs",
	"tottenham hotspur": "spurs",
	"spurs":             "spurs",
	"thfc":              "spurs",

	// Newcastle.
	"newcastle":        "newcastle",
	"newcastle united": "newcastle",
	"nufc":             "newcastle",
	"the magpies":      "newcastle",

	// Aston Villa.
	"aston villa": "aston-villa",
	"villa":       "aston-villa",
	"avfc":        "aston-villa",

	// West Ham.
	"west ham":        "west-ham",
	"west ham united": "west-ham",
	"hammers":         "west-ham",
	"whufc":           "west-ham",

	// Brighton.
	"brighton":                 "brighton",
	"brighton hove albion":     "brighton",
	"brighton and hove albion": "brighton",
	"bhafc":                    "brighton",
	"seagulls":                 "brighton",

	// Everton.
	"everton": "everton",
	"efc":     "everton",
	"toffees": "everton",

	// Wolves.
	"wolves":                   "wolves",
	"wolverhampton":            "wolves",
	"wolverhampton wanderers":  "wolves",
	"wwfc":                     "wolves",

	// Fulham.
	"fulham": "fulham",
	"ffc":    "fulham",

	// Brentford.
	"brentford": "brentford",
	"bfc":       "brentford",

	// Nottingham Forest.
	"nottingham forest": "nottm-forest",
	"nottm forest":      "nottm-forest",
	"forest":            "nottm-forest",
	"nffc":              "nottm-forest",

	// Leicester.
	"leicester":      "leicester",
	"leicester city": "leicester",
	"lcfc":           "leicester",
	"foxes":          "leicester",

	// Leeds United.
	"leeds":        "leeds",
	"leeds united": "leeds",
	"lufc":         "leeds",
	"whites":       "leeds",

	// Burnley.
	"burnley": "burnley",
	"clarets": "burnley",

	// Sunderland.
	"sunderland":    "sunderland",
	"black cats":    "sunderland",
	"safc":          "sunderland",

	// Real Madrid.
	"real madrid": "real-madrid",
	"real":        "real-madrid",
	"los blancos": "real-madrid",
	"los merengues": "real-madrid",

	// Barcelona.
	"barcelona": "barcelona",
	"barca":     "barcelona",
	"barça":     "barcelona",
	"fcb":       "barcelona",

	// Atlético Madrid.
	"atletico madrid":  "atletico-madrid",
	"atletico":         "atletico-madrid",
	"atleti":           "atletico-madrid",
	"atm":              "atletico-madrid",

	// Sevilla.
	"sevilla": "sevilla",
	"sfc":     "sevilla",

	// Valencia.
	"valencia": "valencia",

	// Villarreal.
	"villarreal":      "villarreal",
	"yellow submarine": "villarreal",

	// Real Sociedad.
	"real sociedad": "real-sociedad",
	"la real":       "real-sociedad",

	// Athletic Bilbao.
	"athletic bilbao":  "athletic-bilbao",
	"athletic club":    "athletic-bilbao",
	"athletic":         "athletic-bilbao",

	// Girona.
	"girona":    "girona",
	"girona fc": "girona",

	// Rayo Vallecano.
	"rayo vallecano": "rayo-vallecano",
	"rayo":           "rayo-vallecano",

	// Real Betis.
	"real betis": "betis",
	"betis":      "betis",

	// Celta Vigo.
	"celta vigo": "celta-vigo",
	"celta":      "celta-vigo",

	// Osasuna.
	"osasuna": "osasuna",

	// Getafe.
	"getafe": "getafe",

	// Bayern Munich.
	"bayern":         "bayern",
	"bayern munich":  "bayern",
	"fc bayern":      "bayern",
	"fcb munich":     "bayern",

	// Borussia Dortmund.
	"borussia dortmund": "dortmund",
	"dortmund":          "dortmund",
	"bvb":               "dortmund",

	// RB Leipzig.
	"rb leipzig": "rb-leipzig",
	"leipzig":    "rb-leipzig",
	"rbl":        "rb-leipzig",

	// Bayer Leverkusen.
	"bayer leverkusen": "leverkusen",
	"leverkusen":       "leverkusen",
	"b04":              "leverkusen",

	// Eintracht Frankfurt.
	"eintracht frankfurt": "frankfurt",
	"frankfurt":           "frankfurt",
	"sge":                 "frankfurt",

	// Juventus.
	"juventus": "juventus",
	"juve":     "juventus",
	"the old lady": "juventus",

	// AC Milan.
	"ac milan":  "ac-milan",
	"milan":     "ac-milan",
	"rossoneri": "ac-milan",

	// Inter Milan.
	"inter milan": "inter-milan",
	"inter":       "inter-milan",
	"internazionale": "inter-milan",
	"nerazzurri":  "inter-milan",

	// Napoli.
	"napoli":    "napoli",
	"ssc napoli": "napoli",
	"partenopei": "napoli",

	// AS Roma.
	"as roma":  "roma",
	"roma":     "roma",
	"giallorossi": "roma",

	// Lazio.
	"lazio":     "lazio",
	"ss lazio":  "lazio",
	"biancocelesti": "lazio",

	// PSG.
	"paris saint germain": "psg",
	"paris saint-germain": "psg",
	"psg":                 "psg",
	"paris":               "psg",

	// Marseille.
	"marseille":              "marseille",
	"olympique de marseille": "marseille",
	"om":                     "marseille",

	// Lyon.
	"lyon":               "lyon",
	"olympique lyonnais": "lyon",
	"ol":                 "lyon",

	// Monaco.
	"monaco":    "monaco",
	"as monaco": "monaco",

	// Cleveland Cavaliers.
	"cleveland cavaliers": "cleveland-cavaliers",
	"cavaliers":           "cleveland-cavaliers",
	"cavs":                "cleveland-cavaliers",
	"cleveland":           "cleveland-cavaliers",

	// Los Angeles Lakers.
	"los angeles lakers": "la-lakers",
	"la lakers":          "la-lakers",
	"lakers":             "la-lakers",

	// Boston Celtics.
	"boston celtics": "boston-celtics",
	"celtics":        "boston-celtics",
	"boston":         "boston-celtics",

	// Golden State Warriors.
	"golden state warriors": "golden-state",
	"warriors":              "golden-state",
	"golden state":          "golden-state",
	"gsw":                   "golden-state",

	// Chicago Bulls.
	"chicago bulls": "chicago-bulls",
	"bulls":         "chicago-bulls",
	"chicago":       "chicago-bulls",

	// Miami Heat.
	"miami heat": "miami-heat",
	"heat":       "miami-heat",
	"miami":      "miami-heat",

	// New York Knicks.
	"new york knicks": "new-york-knicks",
	"knicks":          "new-york-knicks",
	"nyk":             "new-york-knicks",

	// Milwaukee Bucks.
	"milwaukee bucks": "milwaukee-bucks",
	"bucks":           "milwaukee-bucks",
	"milwaukee":       "milwaukee-bucks",

	// Denver Nuggets.
	"denver nuggets": "denver-nuggets",
	"nuggets":        "denver-nuggets",
	"denver":         "denver-nuggets",

	// Phoenix Suns.
	"phoenix suns": "phoenix-suns",
	"suns":         "phoenix-suns",
	"phoenix":      "phoenix-suns",

	// OKC Thunder.
	"oklahoma city thunder": "okc-thunder",
	"oklahoma city":         "okc-thunder",
	"okc thunder":           "okc-thunder",
	"thunder":               "okc-thunder",
	"okc":                   "okc-thunder",

	// Indiana Pacers.
	"indiana pacers": "indiana-pacers",
	"pacers":         "indiana-pacers",
	"indiana":        "indiana-pacers",

	// Minnesota Timberwolves.
	"minnesota timberwolves": "minnesota-timberwolves",
	"timberwolves":           "minnesota-timberwolves",
	"twolves":                "minnesota-timberwolves",
	"minnesota":              "minnesota-timberwolves",

	// San Antonio Spurs.
	"san antonio spurs": "san-antonio-spurs",
	"san antonio":       "san-antonio-spurs",

	// Dallas Mavericks.
	"dallas mavericks": "dallas-mavericks",
	"mavericks":        "dallas-mavericks",
	"mavs":             "dallas-mavericks",
	"dallas":           "dallas-mavericks",

	// Philadelphia 76ers.
	"philadelphia 76ers": "philadelphia-76ers",
	"76ers":              "philadelphia-76ers",
	"sixers":             "philadelphia-76ers",
	"philadelphia":       "philadelphia-76ers",

	// Brooklyn Nets.
	"brooklyn nets": "brooklyn-nets",
	"nets":          "brooklyn-nets",
	"brooklyn":      "brooklyn-nets",

	// Toronto Raptors.
	"toronto raptors": "toronto-raptors",
	"raptors":         "toronto-raptors",
	"toronto":         "toronto-raptors",

	// LA Clippers.
	"la clippers":          "la-clippers",
	"los angeles clippers": "la-clippers",
	"clippers":             "la-clippers",

	// New Orleans Pelicans.
	"new orleans pelicans": "new-orleans-pelicans",
	"pelicans":             "new-orleans-pelicans",
	"new orleans":          "new-orleans-pelicans",

	// Memphis Grizzlies.
	"memphis grizzlies": "memphis-grizzlies",
	"grizzlies":         "memphis-grizzlies",
	"memphis":           "memphis-grizzlies",

	// Mumbai Indians.
	"mumbai indians": "mumbai-indians",
	"mi":             "mumbai-indians",
	"mumbai":         "mumbai-indians",

	// Chennai Super Kings.
	"chennai super kings": "chennai-super-kings",
	"csk":                 "chennai-super-kings",
	"chennai":             "chennai-super-kings",

	// Royal Challengers Bengaluru.
	"royal challengers bangalore": "royal-challengers-bangalore",
	"royal challengers bengaluru": "royal-challengers-bangalore",
	"rcb":                        "royal-challengers-bangalore",
	"bangalore":                  "royal-challengers-bangalore",

	// Kolkata Knight Riders.
	"kolkata knight riders": "kolkata-knight-riders",
	"kkr":                   "kolkata-knight-riders",
	"kolkata":               "kolkata-knight-riders",

	// Delhi Capitals.
	"delhi capitals": "delhi-capitals",
	"dc":             "delhi-capitals",
	"delhi":          "delhi-capitals",

	// Punjab Kings.
	"punjab kings": "punjab-kings",
	"pbks":         "punjab-kings",
	"punjab":       "punjab-kings",
	"kxip":         "punjab-kings",

	// Rajasthan Royals.
	"rajasthan royals": "rajasthan-royals",
	"rr":               "rajasthan-royals",
	"rajasthan":        "rajasthan-royals",

	// Sunrisers Hyderabad.
	"sunrisers hyderabad": "sunrisers-hyderabad",
	"srh":                 "sunrisers-hyderabad",
	"hyderabad":           "sunrisers-hyderabad",
	"sunrisers":           "sunrisers-hyderabad",

	// Lucknow Super Giants.
	"lucknow super giants": "lucknow-super-giants",
	"lsg":                  "lucknow-super-giants",
	"lucknow":              "lucknow-super-giants",

	// Gujarat Titans.
	"gujarat titans": "gujarat-titans",
	"gt":             "gujarat-titans",
	"gujarat":        "gujarat-titans",

	// International cricket.
	"india":       "india",
	"team india":  "india",
	"ind":         "india",
	"australia":   "australia",
	"aus":         "australia",
	"england":     "england",
	"eng":         "england",
	"pakistan":    "pakistan",
	"pak":         "pakistan",
	"new zealand": "new-zealand",
	"nz":          "new-zealand",
	"nzl":         "new-zealand",
	"south africa": "south-africa",
	"sa":          "south-africa",
	"west indies": "west-indies",
	"wi":          "west-indies",
	"windies":     "west-indies",
	"sri lanka":   "sri-lanka",
	"sl":          "sri-lanka",
	"bangladesh":  "bangladesh",
	"ban":         "bangladesh",
}

// whitespaceRE collapses runs of whitespace to a single space.
var whitespaceRE = regexp.MustCompile(`\s+`)

// punctRE matches characters we want to drop entirely from a name
// before alias lookup (commas, periods, apostrophes, etc.). We keep
// letters, digits and spaces; everything else is punctuation noise.
var punctRE = regexp.MustCompile(`[^\p{L}\p{N}\s]`)

// suffixesToStrip are tokens that appear at the end of many football
// club names and add no resolution signal ("Manchester United FC" and
// "Manchester United" should both resolve identically). The list is
// applied repeatedly so e.g. "Liverpool F.C. FC" still normalizes
// correctly.
var suffixesToStrip = []string{
	"f c",
	"fc",
	"afc",
	"cf",
	"club",
	"football club",
}

// normalizeTeamName converts a free-text team name into the canonical
// form used as keys in teamAliases. Idempotent: normalize(normalize(x))
// == normalize(x).
//
// Steps:
//  1. Lowercase + trim.
//  2. Remove punctuation (commas, periods, apostrophes...).
//  3. Collapse whitespace.
//  4. Repeatedly strip well-known suffixes ("fc", "afc", "club", ...).
//  5. Final whitespace collapse + trim.
func normalizeTeamName(in string) string {
	s := strings.ToLower(strings.TrimSpace(in))
	if s == "" {
		return ""
	}
	// Drop punctuation first so "F.C." → "f c" → caught by suffix list.
	s = punctRE.ReplaceAllString(s, " ")
	s = whitespaceRE.ReplaceAllString(s, " ")
	s = strings.TrimSpace(s)

	// Strip suffixes iteratively. Re-loop until no suffix matches so
	// stacked tokens like "liverpool fc club" reduce fully.
	for {
		stripped := false
		for _, suf := range suffixesToStrip {
			if s == suf {
				// Don't reduce the entire input to empty.
				break
			}
			if strings.HasSuffix(s, " "+suf) {
				s = strings.TrimSpace(strings.TrimSuffix(s, " "+suf))
				stripped = true
			}
		}
		if !stripped {
			break
		}
	}

	s = whitespaceRE.ReplaceAllString(s, " ")
	return strings.TrimSpace(s)
}

// LookupTeamByName resolves a free-text team name to a canonical
// api.TeamLookup record. Returns (nil, nil) when no match is found
// (callers translate that into a 404). An error is only returned for
// programming mistakes — at the moment, an empty/whitespace query.
//
// Resolution order:
//  1. Alias table (cheap, deterministic, source = "alias").
//  2. If src has an OpenAI fallback wired, that fallback is consulted
//     via the Source.LookupTeamByName method (source = "openai").
//
// The query echoed back in the response is the raw user input (after
// trim) so the TV client can correlate with what it sent. Normalization
// is internal.
func LookupTeamByName(ctx context.Context, src Source, raw string) (*api.TeamLookup, error) {
	q := strings.TrimSpace(raw)
	if q == "" {
		return nil, nil
	}

	if hit, ok := lookupAlias(q); ok {
		out := hit
		out.Query = q
		return &out, nil
	}

	// No alias hit. If the wired source can do its own resolution
	// (OpenAI), defer to it. The stub source returns ErrNotFound here.
	out, err := src.LookupTeamByName(ctx, q)
	if err != nil {
		if err == ErrNotFound {
			return nil, nil
		}
		return nil, err
	}
	return out, nil
}

// lookupAlias is the alias-table-only lookup, exposed separately so
// both the package-level helper and the StubSource can share it.
func lookupAlias(raw string) (api.TeamLookup, bool) {
	key := normalizeTeamName(raw)
	if key == "" {
		return api.TeamLookup{}, false
	}
	canonicalID, ok := teamAliases[key]
	if !ok {
		return api.TeamLookup{}, false
	}
	meta, ok := teamCatalog[canonicalID]
	if !ok {
		// Alias points at an id with no catalog entry — treat as a miss
		// rather than emit a half-populated record.
		return api.TeamLookup{}, false
	}
	return api.TeamLookup{
		Query:         raw,
		TeamID:        meta.ID,
		CanonicalName: meta.CanonicalName,
		League:        meta.League,
		Confidence:    0.95,
		Source:        api.SourceAlias,
	}, true
}

// AliasNames returns every alias key currently registered. Useful for
// diagnostics ("which names will resolve?") and tests.
func AliasNames() []string {
	out := make([]string, 0, len(teamAliases))
	for k := range teamAliases {
		out = append(out, k)
	}
	return out
}
