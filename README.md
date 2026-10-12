# Wysyłka masowa z medycyny pracy (MP Mail)

Aplikacja Windows do miesięcznej wysyłki dokumentów do firm przez SMTP. Jest to osobna wersja projektu `ksg-mail`, z niezależną bazą danych i niezależnym zadaniem Harmonogramu Windows.

## Najważniejsze funkcje

- konfiguracja firm: nazwa, e-mail, temat, indywidualna treść, katalog dokumentów, hasło ZIP, aktywna/nieaktywna;
- automatyczne tworzenie ZIP AES-256 z całej zawartości katalogu firmy;
- SMTP STARTTLS / SSL/TLS, test połączenia i retry;
- podgląd przed wysyłką i zabezpieczenie przed duplikatami w danym miesiącu;
- harmonogram miesięczny przez Harmonogram zadań Windows;
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

Aplikacja dopiero wewnętrznie sprawdza wskazany dzień miesiąca. Jeśli komputer był wyłączony w terminie, późniejsze uruchomienie w tym samym miesiącu nadrabia wysyłkę. Firma, która ma już status Wysłano lub Niepewny dla tego okresu, nie jest automatycznie wysyłana ponownie.

Testy automatyczne sprawdzają:
- moment wejścia harmonogramu w stan „due”;
- XML zadania Windows (codzienny trigger, logowanie, `--scheduled`);
- brak wysyłki przed terminem;
- jedną wysyłkę na firmę w miesiącu;
- nadrobienie po terminie;
- przejście do kolejnego miesiąca;
- import/eksport CSV, cytowanie pól i nieeksportowanie hasła ZIP.

## Dane aplikacji

`%LOCALAPPDATA%\MpMail\mp-mail.db`

Hasła SMTP i ZIP są chronione Windows DPAPI CurrentUser. MP Mail używa osobnego entropy i nie współdzieli bazy ani zadania z KSG Mail.

## Budowanie

Windows x64, .NET SDK 8.0.425+, Inno Setup 6.

```powershell
dotnet restore KsgMail.sln
dotnet test KsgMail.sln -c Release
.\build.ps1 -Version 0.1.0
```

Instalator: `artifacts/installer/MpMail-Setup-0.1.0-win-x64.exe`.

GitHub Actions buduje i testuje każdy push do `main`, a także tworzy instalator i paczkę portable.
