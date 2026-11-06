# FarmingRevamp — kontekst projektu

Mod do Vintage Story (C#, `modinfo.json`: gra 1.21.0, modid `farmingrevamp`). Zastępuje vanillowe oranie i sianie
ręczną pracą na siatce wokseli: darń → oranie bruzd → sianie → zakrywanie nasion → pole z uprawą.

Rozmowa z autorem toczy się po polsku; kod, komentarze i identyfikatory po angielsku.

## Uruchamianie

- `./run.sh` — buduje (`dotnet build FarmingRevamp`) i odpala windowsowy klient gry z WSL z modem
  (`--addModPath bin/Debug/Mods`) i assetami prosto ze źródeł (`--addOrigin FarmingRevamp/assets`).
  Wymaga zmiennej `VINTAGE_STORY` (folder gry). Dodatkowe argumenty idą do gry.
- `FarmingRevamp/Properties/launchSettings.json` — profile Client/Server do IDE.
- Odświeżanie bez restartu: tekstury/kształty/lang przez `.reload …` w czacie; blocktypes/itemtypes/patches/recipes
  wymagają ponownego wejścia do świata; zmiany w C# — restartu gry.
- Testów automatycznych nie ma; weryfikacja to build + gra.

## Cykl uprawy (co już działa)

1. **Darń → goła ziemia** — łopata, przytrzymanie PPM na glebie z trawą lub ściółce leśnej
   (`CollectibleBehaviorSodDigging`). Czas zależy od szybkości kopania łopaty. Szansa na kamień miejscowej skały
   (zależna od głębokości skały, żyzności, twardości skały, wysokości n.p.m.) i na patyki ze ściółki.
2. **Oranie** — motyka PPM na gołej ziemi (`soil-*-none`, wolne miejsce nad) zamienia ją w `tillingsoil-{fertility}-{side}`;
   kierunek rzędów (`ns`/`we`) wg kierunku patrzenia. Gleba z darnią jest odrzucana komunikatem.
   Vanillowe oranie motyką zostaje pod Shift+Ctrl.
3. **Kopanie bruzd** (`BlockEntityTillingSoil`) — bruzda 2 woksele szerokości, 1 głębokości, w dowolnym miejscu
   w poprzek bloku. 3 zalecane bruzdy (x = 2–3, 7–8, 12–13) podświetlone na pomarańczowo, reszta kopalnych wokseli
   na szaro. Wykopana ziemia usypuje się w wał po obu stronach i może zasypać sąsiednią bruzdę. Po wykopaniu
   3 zalecanych bruzd blok staje się `farmingrevamp:farmland-dry-{fertility}-{side}-open`.
   Grabie/łopata wyrównują tu glebę w swoim pasie.
4. **Sianie** — nasiona PPM na pustym polu zużywają 1 nasiono i zamieniają je w `sowingfarmland`
   (`ItemSowingSeed`, `BlockEntitySowingFarmland`); potem PPM nasionami w 9 podświetlonych miejsc (3 na bruzdę).
   Nasiona rysowane jako woksele w kolorze z tekstury nasiona. Papryka (`bellpepper`) zostaje przy vanilli.
5. **Zakrywanie** — po zasianiu wszystkich 9 podświetlają się grzbiety (6 kolumn wałów przy bruzdach); zapadają się
   do sąsiedniej bruzdy. Po zakryciu: `farmland-{state}-{fertility}-{side}-covered` z posadzoną uprawą
   i przywróconymi danymi pola (składniki, wilgoć).
6. **Po zbiorze** — `BlockFurrowedFarmland` otwiera bruzdy (`-covered` → `-open`), gdy nad polem robi się pusto;
   obumarła roślina dalej trzyma je zakryte. Pole gotowe do ponownego siewu.

Vanillowa `farmland` dostaje kształt z bruzdami (patch); nie ma wariantu `side`, traktowana jako `ns`.

## Narzędzia i szerokość robocza

Szerokość = ile kolumn siatki w poprzek narzędzie obrabia naraz (`WorkWidth`). Przytrzymanie pracuje wzdłuż rzędów
od dalszego końca w stronę gracza, cały rząd w ~1,2 s (`FurrowMs`), puszczenie pauzuje, kolejne wznawia.

| Narzędzie | Oranie (`tillingsoil`) | Zakrywanie (`sowingfarmland`) |
|---|---|---|
| Motyka — tryb „Cała bruzda” | bruzda pod kursorem; na zalecanej bruzdzie przyciąga do niej | 1 grzbiet |
| Motyka — tryb „Malowanie” | każdy woksel, nad którym przejedzie kursor | j.w. |
| Łopata (`width: 4`) | wyrównuje pas 4 wokseli | pas 4 wokseli (bruzda + oba grzbiety) |
| Grabie (`width: 9`) | wyrównuje pas 9 wokseli | pas 9 wokseli (2 bruzdy) |
| Pusta ręka | — | 1 woksel na kliknięcie |

- Tryby motyki: klawisz F (wybór trybu narzędzia), zapisywane na przedmiocie (atrybut `toolMode`, 0 = malowanie,
  domyślny).
- Szerokość łopaty/grabi ustawia JSON (`farmingrevamp.SeedCovering` → `properties.width`); kolejne narzędzie do
  zakrywania = tylko wpis w JSON.
- Ukończenie pola zużywa narzędzie trzymane w ręce.
- Grabie: `itemtypes/rake.json`, receptura z 4 patyków (`SSS,_S_`), wytrzymałość 60, model złożony ręcznie
  (trzonek z motyki + poprzeczka + 5 zębów). Docelowo mają też spulchniać glebę pod zboża jako alternatywa dla bruzd —
  **jeszcze niezrobione**.

## Architektura

- `TillingGrid` — mapa wysokości 16×16 (wysokości w wokselach). Powierzchnia 15 (1 niżej, jak vanillowe pole),
  dno bruzdy 14, wały do 18. Rzędy biegną wzdłuż z, z = 2…13 (margines 2 na końcach). Operacje: `DigStep`
  (kopanie + wały), `CollapseStep` (grzbiet → bruzda), `Level` (wyrównanie z zachowaniem ilości ziemi),
  `Tilled()` (siatka standardowo zaoranego pola).
- `BlockEntityFurrowWork` — wspólna baza obu pól: render kolumn wyciętych z sześcianu `ColumnBlock` (z przycinaniem UV),
  „linie” (zestawy kolumn: bruzdy lub grzbiety), przytrzymanie/malowanie, pakiety, ukończenie. Na bloku `we` siatka
  jest transponowana (x↔z).
- Pakiety klient → serwer: 1000 start przytrzymania (lista linii), 1001 stop, 1002 zasianie miejsca, 1003 jeden
  woksel. Klient wysyła linie/woksele sam, bo pozycja trafienia na serwerze jest niedokładna (jak w knappingu);
  serwer sprawdza zakres, claimy, narzędzie i szerokość.
- `TillingOutlineRenderer` — ramki wokseli jak w knappingu: pomarańczowe (do zrobienia), szare (opcjonalne).
- `BlockFurrowWork` — blok obu pól: strefy zaznaczenia z block entity, kliknięcie pustą ręką.
- Rejestracja klas: `FarmingRevampModSystem.Start`.
- Assety: `blocktypes/` (`farmland`, `tillingsoil`, `sowingfarmland`), `patches/` (hoe, shovel, seeds, farmland),
  `itemtypes/rake.json`, `recipes/grid/rake.json`, `shapes/`, `lang/en.json` + `pl.json`.

## Założenia projektowe (ustalone z autorem)

- Praca ręczna i fizyczna: ziemia się przemieszcza (wały, zasypywanie, wyrównywanie), a nie znika.
- Pełna dowolność gracza przy kopaniu; zalecany układ jest tylko podpowiedzią (kolory ramek).
- Narzędzie decyduje o szerokości roboczej: ręka < motyka < łopata < grabie.
- Bez zbędnych komunikatów, gdy podświetlenie mówi samo za siebie.
- Animacja motyki przy pracy na polu jest na razie wyłączona.

## Znane ograniczenia / dług

- Gotowe pole ma zawsze standardowy kształt — dodatkowe bruzdy wykopane poza zalecanymi przepadają po ukończeniu.
- Wał wpadający do zalecanej bruzdy cofa jej postęp.
- Kopanie/zakrywanie tylko w z = 2…13.
- Malowanie nie ma limitu tempa (`shortcut:` w `BlockEntityFurrowWork.cs`).
- Brak animacji użycia motyki na polu (`shortcut:` w `ItemTillingHoe.cs`).
- Grabie pomijają pas, w którym nie ma już nic do wykopania.
- `modinfo.json`: autor „Unknown”, opis „To be added”; w `FarmingRevampModSystem` zostały logi „Hello from template mod”.

## Do ustalenia z autorem

- Spulchnianie gleby grabiami pod zboża — mechanika, wynik, dla jakich upraw.
- Docelowy kształt gotowego pola przy swobodnych bruzdach (czy zapamiętywać własną siatkę).
- Czy łopata ma wyrównywać glebę przy oraniu (teraz tak, jako efekt uboczny wspólnego zachowania).
- Tempo/koszt pracy (limit dla malowania, wpływ materiału narzędzia, zużycie).
- Czy zbicie pola w trakcie siania (`sowingfarmland`, `drops: []`) ma coś zwracać.
- Docelowa animacja pracy motyką.
