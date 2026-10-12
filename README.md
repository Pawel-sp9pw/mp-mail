# Wysyłka masowa z medycyny pracy (MP Mail)

Aplikacja Windows do codziennej wysyłki dokumentów do firm przez SMTP. Jest to osobna wersja projektu `ksg-mail`, z niezależną bazą danych i niezależnym zadaniem Harmonogramu Windows.

## Najważniejsze funkcje

- konfiguracja firm: nazwa, e-mail, temat, indywidualna treść, katalog dokumentów, hasło ZIP, aktywna/nieaktywna;
- automatyczne tworzenie ZIP AES-256 wyłącznie z plików pasujących do `orzeczenie-profilaktyczne-p1*.pdf` (zawsze z pominięciem katalogu `archiwum`);
- SMTP STARTTLS / SSL/TLS, test połączenia i retry;
- podgląd przed wysyłką i zabezpieczenie przed duplikatami w danym dniu;
- harmonogram codzienny przez Harmonogram zadań Windows;
- po potwierdzonej poprawnej wysyłce przeniesienie wysłanych plików do `archiwum/<okres>/` pod katalogiem danej firmy;
- historia wysyłek;
- import i eksport listy firm do CSV.

## CSV

Separator: średnik, kodowanie UTF-8. Kolumny:

```
id;nazwa;email;temat;tresc;katalog;haslo_zip;aktywny
```

Eksport celowo pozostawia `haslo_zip` puste — hasła nie są eksportowane w jawnej postaci. Przy ponownym imporcie istniejącej firmy puste `haslo_zip` zachowuje hasło zapisane lokalnie. Nowa firma musi mieć hasło podane w CSV.

Kolumna `aktywny`: `1/0`, `tak/nie`, `true/false`.

## Harmonogram

Po włączeniu harmonogramu i zapisaniu konfiguracji aplikacja tworzy zadanie:

`MP-Mail-<SID użytkownika>`

Zadanie uruchamia `MpMail.exe --scheduled`:
- codziennie o skonfigurowanej godzinie;
- przy logowaniu użytkownika.

Codziennie po osiągnięciu skonfigurowanej godziny aplikacja wysyła dokumenty do aktywnych firm. Jeśli komputer był wyłączony o tej godzinie, uruchomienie zadania przy późniejszym logowaniu może wykonać wysyłkę tego samego dnia. Firma, która ma już status Wysłano lub Niepewny dla danego dnia, nie jest automatycznie wysyłana ponownie. Po statusie Wysłano dokładnie te pliki, które trafiły do ZIP, są przenoszone do `archiwum/<RRRR-MM-DD>/`. Katalog `archiwum` i jego podkatalogi nigdy nie są brane pod uwagę przy kolejnych paczkach. Inne pliki niż `orzeczenie-profilaktyczne-p1*.pdf` pozostają w katalogu firmy i nie są pakowane ani przenoszone.

Testy automatyczne sprawdzają:
- moment wejścia harmonogramu w stan „due”;
- XML zadania Windows (codzienny trigger, logowanie, `--scheduled`);
- brak wysyłki przed terminem;
- jedną wysyłkę na firmę dziennie;
- brak wysyłki przed ustawioną godziną;
- ponowną wysyłkę następnego dnia;
- filtrowanie plików do wzorca `orzeczenie-profilaktyczne-p1*.pdf`;
- przeniesienie wysłanych plików do archiwum oraz pomijanie archiwum przy pakowaniu;
- import/eksport CSV, cytowanie pól i nieeksportowanie hasła ZIP.

## Dane aplikacji

`%LOCALAPPDATA%\MpMail\mp-mail.db`

Hasła SMTP i ZIP są chronione Windows DPAPI CurrentUser. MP Mail używa osobnego entropy i nie współdzieli bazy ani zadania z KSG Mail.

## Budowanie

Windows x64, .NET SDK 8.0.425+, Inno Setup 6.

```powershell
dotnet restore KsgMail.sln
dotnet test KsgMail.sln -c Release
.\build.ps1 -Version 0.4.0
```

Instalator: `artifacts/installer/MpMail-Setup-0.4.0-win-x64.exe`.

GitHub Actions buduje i testuje każdy push do `main`, a także tworzy instalator i paczkę portable.
