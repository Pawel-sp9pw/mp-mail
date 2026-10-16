# Wysyłka masowa z medycyny pracy (MP Mail)

Aplikacja Windows do codziennej wysyłki dokumentów do firm przez SMTP. Jest to osobna wersja projektu `ksg-mail`, z niezależną bazą danych i niezależnym zadaniem Harmonogramu Windows.

## Najważniejsze funkcje

- konfiguracja firm: nazwa, e-mail, temat, indywidualna treść, katalog dokumentów, hasło ZIP, aktywna/nieaktywna;
- filtrowanie firm po nazwie/e-mailu oraz statusie aktywna/nieaktywna;
- wielokrotny wybór firm, zaznaczanie wszystkich widocznych i operacje masowe: aktywacja, dezaktywacja i usuwanie;
- skanowanie wyłącznie plików bezpośrednio w katalogu firmy — podkatalogi nie są przeszukiwane;
- zwykła wysyłka zbiorcza obejmuje `orzeczenie-profilaktyczne-p1*.pdf`, ale z wyłączeniem plików z prefiksem `NEG.`;
- pliki `NEG.orzeczenie-profilaktyczne-p1*.pdf` są pakowane osobno do szyfrowanego ZIP i wysyłane według konfigurowalnego interwału (domyślnie co 15 minut);
- SMTP STARTTLS / SSL/TLS, test połączenia i retry;
- podgląd przed wysyłką i zabezpieczenie przed duplikatami w danym dniu;
- harmonogram zbiorczy o jednej lub wielu stałych godzinach dziennie oraz dodatkowy interwał NEG przez Harmonogram zadań Windows;
- po potwierdzonej poprawnej wysyłce przeniesienie wysłanych plików do `archiwum/<okres>/` pod katalogiem danej firmy;
- konfigurowalna retencja archiwum (domyślnie 90 dni) z automatycznym usuwaniem przeterminowanych plików i pustych katalogów;
- historia wysyłek;
- import i eksport listy firm do CSV;
- automatyczne i ręczne sprawdzanie aktualizacji przez GitHub Releases;
- bezpieczne pobieranie i instalowanie aktualizacji po weryfikacji SHA-256.

## CSV

Separator: średnik, kodowanie UTF-8. Kolumny:

```
id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny
```

Eksport celowo pozostawia `haslo_zip` puste — hasła nie są eksportowane w jawnej postaci. Przy ponownym imporcie istniejącej firmy puste `haslo_zip` zachowuje hasło zapisane lokalnie. Jeżeli nowa firma nie ma e-maila lub hasła ZIP, zostanie zaimportowana jako nieaktywna. Przy ponownym imporcie istniejącej firmy puste `haslo_zip` nadal zachowuje hasło zapisane lokalnie.

Kolumna `aktywny`: `1/0`, `tak/nie`, `true/false`.

Podczas importu dolny pasek pokazuje nazwę aktualnie przetwarzanej lub zapisywanej firmy. Po zatwierdzeniu importu wyświetla liczbę nowych i zaktualizowanych firm oraz ich nazwy. Dłuższą listę można przewijać. Błędy rekordu wskazują numer wiersza CSV i nazwę firmy; błędy zapisu również wskazują firmę. Import jest transakcyjny: błąd powoduje wycofanie całego zapisu. Przy ręcznym zapisie firmy jej nazwa jest widoczna zarówno w potwierdzeniu, jak i w komunikacie błędu (brak nazwy oznaczany jest jako „bez nazwy”).

## Harmonogram

Po włączeniu harmonogramu i zapisaniu konfiguracji aplikacja tworzy zadanie:

`MP-Mail-<SID użytkownika>`

Zadanie uruchamia `MpMail.exe --scheduled`:
- codziennie o każdej skonfigurowanej godzinie wysyłki zbiorczej;
- cyklicznie zgodnie z interwałem NEG (domyślnie co 15 minut);
- przy logowaniu użytkownika.

W polu **Godziny wysyłki zbiorczej** wpisz np. `09:00, 13:00, 16:30`. Godziny muszą mieć format `HH:mm`; można je rozdzielać przecinkiem, średnikiem lub spacją. Powtórzenia są usuwane, a godziny sortowane. Dotychczas zapisana pojedyncza godzina pozostaje poprawna.

Podczas automatycznych uruchomień aplikacja najpierw wykonuje retencję archiwum dla wszystkich skonfigurowanych firm. Pliki starsze niż ustawiona liczba dni są usuwane, a puste katalogi archiwum sprzątane. Czas retencji liczony jest od momentu przeniesienia pliku do archiwum. Po osiągnięciu każdej skonfigurowanej godziny aplikacja wysyła nowe dokumenty do aktywnych firm, najwyżej raz dla danej firmy i terminu. Po przerwie w pracy komputera nadrabiany jest wyłącznie ostatni osiągnięty termin tego dnia. Trwająca lub niepewna wcześniejsza wysyłka zbiorcza blokuje pozostałe terminy tego samego dnia, aby nie powielać dokumentów. Po statusie Wysłano dokładnie te pliki, które trafiły do ZIP, są przenoszone do `archiwum/<okres>/`: pierwszy termin zachowuje okres `RRRR-MM-DD`, kolejne używają `SCHED-RRRR-MM-DD-HHmm`. Dzięki temu wcześniejsze zapisy pojedynczej wysyłki nadal chronią pierwszy termin przed powtórzeniem. Program nie przeszukuje żadnych podkatalogów firmy. `orzeczenie-profilaktyczne-p1*.pdf` bez prefiksu `NEG.` są wysyłane zbiorczo według harmonogramu dziennego. `NEG.orzeczenie-profilaktyczne-p1*.pdf` są wysyłane wyłącznie w trybie interwałowym. Pozostałe pliki pozostają w katalogu firmy i nie są pakowane ani przenoszone.

Testy automatyczne sprawdzają:
- moment wejścia harmonogramu w stan „due”;
- XML zadania Windows (codzienny trigger, logowanie, `--scheduled`);
- brak wysyłki przed terminem;
- jedną wysyłkę na firmę w każdym ustawionym terminie i wysyłkę nowych dokumentów w kolejnym terminie;
- brak wysyłki przed ustawioną godziną;
- ponowną wysyłkę następnego dnia;
- brak przeszukiwania podkatalogów;
- rozdzielenie zwykłych i `NEG.` plików;
- wysyłkę `NEG.` zgodnie z interwałem;
- przeniesienie wysłanych plików do archiwum oraz pomijanie archiwum przy pakowaniu;
- retencję archiwum, usuwanie przeterminowanych plików i pustych katalogów;
- import/eksport CSV, cytowanie pól i nieeksportowanie hasła ZIP;
- nazwy firm i numery wierszy w błędach importu oraz wycofanie zapisu przy błędzie firmy;
- wiele godzin w zadaniu Windows, deduplikację i walidację godzin, nadrabianie ostatniego terminu oraz blokadę kolejnych terminów po niepewnej wysyłce.

## Dane aplikacji

`%LOCALAPPDATA%\MpMail\mp-mail.db`

Hasła SMTP i ZIP są chronione Windows DPAPI CurrentUser. MP Mail używa osobnego entropy i nie współdzieli bazy ani zadania z KSG Mail.

## Budowanie

Windows x64, .NET SDK 8.0.425+, Inno Setup 6.

```powershell
dotnet restore KsgMail.sln
dotnet test KsgMail.sln -c Release
.\build.ps1 -Version 0.9.0
```

Instalator: `artifacts/installer/MpMail-Setup-0.9.0-win-x64.exe`.

GitHub Actions buduje i testuje każdy push do `main`, a także tworzy instalator i paczkę portable.


## Aktualizacje

MP Mail automatycznie sprawdza przy uruchomieniu najnowszy stabilny GitHub Release repozytorium `Pawel-sp9pw/mp-mail`. W zakładce **Aktualizacje** można również wykonać sprawdzenie ręczne.

Jeśli dostępna jest nowsza wersja, aplikacja:
1. pobiera instalator `MpMail-Setup-<wersja>-win-x64.exe` z GitHub Release,
2. pobiera `SHA256SUMS.txt` z tego samego wydania,
3. weryfikuje SHA-256 instalatora,
4. uruchamia instalator dopiero po poprawnej weryfikacji,
5. zamyka MP Mail, aby instalator mógł bezpiecznie zastąpić pliki programu,
6. po pomyślnej instalacji uruchamia zainstalowany `MpMail.exe` automatycznie, również w trybie `/SILENT` i `/VERYSILENT`.

Uruchomienie jest wykonywane przez sekcję `[Run]` instalatora dopiero po udanej instalacji. W razie błędu instalacji program nie jest uruchamiany. Zmiana działa także podczas aktualizacji ze starszej wersji MP Mail, ponieważ odpowiada za nią pobrany instalator. W instalacji z kreatorem nadal można odznaczyć uruchomienie programu na ekranie końcowym.

Test `tests/Test-InstallerRestart.ps1` buduje instalator z niewysyłającą maili aplikacją testową i sprawdza oba tryby cichej instalacji: jedno uruchomienie z docelowego katalogu po sukcesie oraz brak uruchomienia po błędzie zapisu dodatkowego pliku, gdy w katalogu znajduje się już działająca poprzednia instalacja. Test używa osobnego identyfikatora instalatora i katalogów pod `artifacts/installer-tests`, bez skrótów i wpisu deinstalacji. Jest wykonywany przed publikacją wydania w GitHub Actions oraz podczas `build.ps1`.

Nie jest używany żaden zewnętrzny serwer aktualizacji poza GitHub Releases projektu.
