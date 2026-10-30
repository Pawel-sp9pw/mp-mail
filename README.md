# Wysyłka masowa z medycyny pracy (MP Mail)

Aplikacja Windows do codziennej wysyłki dokumentów do firm przez SMTP. Jest to osobna wersja projektu `ksg-mail`, z niezależną bazą danych i niezależnym zadaniem Harmonogramu Windows.

## Najważniejsze funkcje

- konfiguracja firm: nazwa, e-mail, temat, indywidualna treść, katalog dokumentów, hasło ZIP, aktywna/nieaktywna;
- filtrowanie firm po nazwie/e-mailu oraz statusie aktywna/nieaktywna;
- wielokrotny wybór firm, zaznaczanie wszystkich widocznych i operacje masowe: aktywacja, dezaktywacja i usuwanie;
- skanowanie wyłącznie plików bezpośrednio w katalogu firmy — podkatalogi nie są przeszukiwane;
- konfigurowalne reguły plików z obsługą wzorców `*` i `?`; reguły można dodawać, usuwać, wyłączać i zmieniać ich kolejność;
- weryfikacja pracodawcy w każdym wysyłanym PDF: program odczytuje pole `(nazwa i adres pracodawcy)`, bierze część przed pierwszym przecinkiem i porównuje ją z nazwą firmy w bazie; niezgodny lub nierozpoznawalny PDF blokuje paczkę tej firmy;
- każda reguła może działać wg skonfigurowanych godzin zbiorczych albo we własnym interwale minutowym; reguły z tym samym interwałem są pakowane razem;
- po aktualizacji zachowane są dotychczasowe reguły: `orzeczenie-profilaktyczne-p1*.pdf` wg godzin oraz `NEG.orzeczenie-profilaktyczne-p1*.pdf` w interwale 15 minut (lub dotychczas skonfigurowanym);
- SMTP STARTTLS / SSL/TLS, test połączenia i retry;
- podgląd przed wysyłką i zabezpieczenie przed duplikatami w danym dniu;
- harmonogram zbiorczy o jednej lub wielu stałych godzinach dziennie oraz dowolna liczba unikalnych interwałów reguł plików przez Harmonogram zadań Windows;
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
- codziennie o każdej skonfigurowanej godzinie, jeśli istnieje aktywna reguła „Wg godzin”;
- cyklicznie dla każdego unikalnego interwału aktywnych reguł, np. 5, 15 i 60 minut;
- przy logowaniu użytkownika.

Automat działa przy zablokowanym ekranie, jeśli użytkownik pozostaje zalogowany. Program nie musi mieć widocznego okna. Jeżeli MP Mail jest otwarty, uruchomienie `--scheduled` przekazuje żądanie do działającej instancji. Żądanie czeka na zakończenie trwającej operacji, a następnie wymusza sprawdzenie harmonogramu. Jeśli ustawiona minuta już minęła, zwykła partia nie zostanie rozpoczęta. Pozostawiony podgląd nie zatrzymuje automatu: przed automatyczną wysyłką zostaje wyczyszczony, aby nie można było ręcznie wysłać nieaktualnego ZIP. Podgląd pozostaje dostępny, gdy automat nie ma nic do wysłania. Wysyłka zbiorcza jest sprawdzana przed retencją i partią NEG.

Zadanie Windows ma włączone `WakeToRun`; wybudzenie z uśpienia zależy również od ustawień zasilania i możliwości sprzętu. `StartWhenAvailable=false` wyłącza nadrabianie pominiętych uruchomień przez Windows. Niezależnie od źródła uruchomienia aplikacja dodatkowo sprawdza ustawioną minutę. Po uruchomieniu okna MP Mail zadanie jest odświeżane automatycznie, więc aktualizacja obejmuje też istniejącą konfigurację. Wylogowanie użytkownika nadal uniemożliwia działanie zadania typu `InteractiveToken`. Program nie jest usługą Windows: wysyłka również po wylogowaniu wymaga osobnego wdrożenia konta usługi, dostępu do danych i haseł chronionych DPAPI CurrentUser.

Diagnostyka automatu jest zapisywana w `%LOCALAPPDATA%\MpMail\Logs\schedule-RRRR-MM-DD.log`. Zawiera uruchomienia zadania, przekazanie żądań do okna, godziny sprawdzenia, liczby firm gotowych do wysyłki i przyczyny pominięcia (nieaktywność, blokada powtórzenia, niepewny wynik, brak pasujących dokumentów/niedostępny katalog) oraz błędy. Log nie zawiera haseł. Szczegółowe wyniki SMTP pozostają w zakładce Historia. Brak wpisu w Historii nie oznacza już braku śladu diagnostycznego sprawdzenia harmonogramu.

W polu **Godziny wysyłki zbiorczej** wpisz np. `09:00, 13:00, 16:30`. Godziny muszą mieć format `HH:mm`; można je rozdzielać przecinkiem, średnikiem lub spacją. Powtórzenia są usuwane, a godziny sortowane. Dotychczas zapisana pojedyncza godzina pozostaje poprawna.

### Reguły plików

W sekcji **Reguły plików** można dodać dowolną nazwę lub wzorzec pliku. Obsługiwane są symbole `*` i `?`, np. `wynik-*.pdf`, `DOK-????.pdf` albo dokładna nazwa `raport.pdf`. Regułę można włączyć/wyłączyć, usunąć i przesuwać w górę lub w dół.

Kolejność ma znaczenie: jeśli jeden plik pasuje do kilku aktywnych reguł, używana jest **pierwsza pasująca reguła**. Dzięki temu plik nie zostanie wysłany dwa razy. Tryb **Wg godzin** dołącza plik do zbiorczej partii uruchamianej o skonfigurowanych godzinach. Tryb **Interwał** wysyła go zgodnie z liczbą minut ustawioną w kolumnie `Min.`. Wszystkie reguły z tym samym interwałem są łączone w jeden ZIP dla danej firmy; różne interwały tworzą osobne partie.

Po aktualizacji istniejąca konfiguracja zostaje automatycznie odwzorowana na reguły: `orzeczenie-profilaktyczne-p1*.pdf` pozostaje regułą wg godzin, a `NEG.orzeczenie-profilaktyczne-p1*.pdf` pozostaje regułą interwałową. Jeśli wcześniej zmieniono interwał NEG, ta wartość jest zachowana. Program nadal bierze pod uwagę wyłącznie pliki bezpośrednio w katalogu firmy i nie przeszukuje podkatalogów.

Zwykłe dokumenty są wysyłane wyłącznie jedną partią zbiorczą w każdym ustawionym terminie. Start jest dozwolony tylko w skonfigurowanej minucie: dla `13:00` od `13:00:00` do `13:00:59`. Od `13:01` partia nie zostanie rozpoczęta. Po aktualizacji, restarcie, logowaniu lub wybudzeniu pominięte terminy nie są nadrabiane. Rozpoczęta punktualnie partia może trwać dłużej niż minutę: wszystkie wiadomości i ZIP-y są przygotowywane przed wysłaniem pierwszej wiadomości. Każda firma otrzymuje własną wiadomość i ZIP; dokumenty różnych firm nie są łączone.

Rejestr rozpoczęcia partii jest zapisywany atomowo w bazie, również gdy nie było dokumentów do wysłania. Kolejne sprawdzenie, ponowny sygnał Windows lub restart w tej samej minucie nie uruchamia dodatkowej partii. Nowe firmy i dokumenty dodane po zebraniu partii czekają na następny ustawiony termin. Nieudana wysyłka nie uruchamia kolejnego indywidualnego przebiegu w tym samym terminie; próby SMTP wewnątrz rozpoczętej partii zachowują skonfigurowany limit retry. Poza godzinami zbiorczymi mogą być wysyłane pliki przypisane do aktywnych reguł typu **Interwał**. Domyślna reguła NEG nadal działa jak wcześniej.

Po sprawdzeniu wysyłki zbiorczej aplikacja wykonuje retencję archiwum dla wszystkich skonfigurowanych firm. Pliki starsze niż ustawiona liczba dni są usuwane, a puste katalogi archiwum sprzątane. Czas retencji liczony jest od momentu przeniesienia pliku do archiwum. Trwająca lub niepewna wcześniejsza wysyłka zbiorcza blokuje pozostałe terminy tego samego dnia, aby nie powielać dokumentów. Po statusie Wysłano dokładnie te pliki, które trafiły do ZIP, są przenoszone do `archiwum/<okres>/`: pierwszy termin zachowuje okres `RRRR-MM-DD`, kolejne używają `SCHED-RRRR-MM-DD-HHmm`. Dzięki temu wcześniejsze zapisy pojedynczej wysyłki nadal chronią pierwszy termin przed powtórzeniem. Program nie przeszukuje żadnych podkatalogów firmy. Pozostałe pliki pozostają w katalogu firmy i nie są pakowane ani przenoszone.

Wersja 0.11.1 dodaje konfigurowalne reguły plików i zachowuje zgodność z dotychczasową konfiguracją standard/NEG. Istniejące firmy, ustawienia SMTP, zaszyfrowane hasła, historia, retencja i blokady wysyłki pozostają zachowane.

Testy automatyczne sprawdzają:
- moment wejścia harmonogramu w stan „due”;
- XML zadania Windows (codzienny trigger, logowanie, `--scheduled`);
- brak wysyłki przed terminem;
- jedną wysyłkę na firmę w każdym ustawionym terminie i wysyłkę nowych dokumentów w kolejnym terminie;
- brak wysyłki przed ustawioną godziną;
- ponowną wysyłkę następnego dnia;
- brak przeszukiwania podkatalogów;
- domyślne reguły standard/NEG oraz własne wzorce z `*` i `?`;
- wyłączanie reguł, priorytet pierwszej pasującej reguły oraz różne interwały wysyłki;
- grupowanie reguł o tym samym interwale i oddzielne partie dla różnych interwałów;
- przeniesienie wysłanych plików do archiwum oraz pomijanie archiwum przy pakowaniu;
- retencję archiwum, usuwanie przeterminowanych plików i pustych katalogów;
- import/eksport CSV, cytowanie pól i nieeksportowanie hasła ZIP;
- nazwy firm i numery wierszy w błędach importu oraz wycofanie zapisu przy błędzie firmy;
- wiele godzin w zadaniu Windows, deduplikację i walidację godzin, brak nadrabiania terminów oraz blokadę kolejnych terminów po niepewnej wysyłce;
- wysyłkę o 12:00 i 18:00 z niewidocznym oknem i pozostawionym podglądem;
- przekazanie sygnału zadania Windows do działającego okna, oczekiwanie na koniec operacji oraz brak duplikatów po powtórnym sygnale;
- wybudzanie w XML zadania, priorytet wysyłki zbiorczej przed NEG i diagnostykę pustego przebiegu;
- brak wysyłki zwykłych dokumentów poza skonfigurowaną minutą oraz zachowanie osobnego interwału NEG;
- przygotowanie wszystkich ZIP-ów przed pierwszą wiadomością i brak dokładania firm/plików do zakończonej partii, również po restarcie;
- atomową blokadę ponownego uruchomienia partii oraz zachowanie danych istniejącej bazy przy rozszerzeniu schematu.

## Dane aplikacji

`%LOCALAPPDATA%\MpMail\mp-mail.db`

Hasła SMTP i ZIP są chronione Windows DPAPI CurrentUser. MP Mail używa osobnego entropy i nie współdzieli bazy ani zadania z KSG Mail.

## Budowanie

Windows x64, .NET SDK 8.0.425+, Inno Setup 6.

```powershell
dotnet restore KsgMail.sln
dotnet test KsgMail.sln -c Release
.\build.ps1 -Version 0.11.1
```

Instalator: `artifacts/installer/MpMail-Setup-0.11.1-win-x64.exe`.

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


### Znaczniki daty

- `{data}` oznacza rzeczywistą lokalną datę przygotowania/wysyłki wiadomości, np. `2026-10-22`.
- `{miesiac}` nadal oznacza miesiąc wybranego okresu, np. `2026-10`.



## Weryfikacja pracodawcy w PDF

Przed utworzeniem ZIP-a MP Mail odczytuje tekst z każdego pliku PDF zakwalifikowanego do wysyłki. Wyszukuje frazę **„do pracy w”**, od niej odczytuje wartość pracodawcy aż do pola **„nazwa i adres pracodawcy”**, a następnie bierze nazwę firmy znajdującą się przed pierwszym przecinkiem. Nazwa jest porównywana z polem **Nazwa firmy** zapisanym w MP Mail po ujednoliceniu wielkości liter i wielokrotnych spacji.

Jeżeli pola nie można odczytać albo nazwa pracodawcy z PDF różni się od nazwy firmy w bazie, plik nie jest pakowany ani wysyłany, a firma otrzymuje status błędu w podglądzie/historii.

W ustawieniach SMTP można opcjonalnie podać **Adres e-mail do powiadomień o błędach**. Na ten adres MP Mail wysyła krótkie powiadomienie o błędach i wynikach niepewnych związanych z wysyłką, m.in. o niezgodności pracodawcy w PDF. Powiadomienia nie zawierają załączników.



### Czytelność błędów

Długie komunikaty błędów można przewijać w dolnym polu statusu. W podglądzie po zaznaczeniu wiersza z błędem pełny opis jest również wyświetlany w przewijanym polu szczegółów.
