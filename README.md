# SpaceManager

Application Windows pour visualiser la taille effective des dossiers et de leurs sous-dossiers, lancée depuis n'importe quel dossier ou disque via le menu contextuel.

## Fonctionnalités

- Arborescence des dossiers et fichiers avec taille affichée
- Barres visuelles d'utilisation (proportionnelles aux éléments du même niveau)
- Tableau triable par nom ou par taille (clic sur les en-têtes de colonnes)
- Calcul récursif de la taille de chaque dossier (contenu total)
- Chargement paresseux à l'expansion d'un dossier
- Lancement depuis le menu contextuel Windows (dossier, fond de dossier, disque)
- Ouverture manuelle d'un dossier depuis l'application

## Prérequis

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) ou runtime Desktop

## Icône

L'application embarque une icône (dossier + barres de taille) visible dans :
- l'en-tête de la fenêtre
- la barre des tâches
- le menu contextuel Windows (après installation)

Si le menu contextuel était déjà installé avant l'ajout de l'icône, relancez l'application pour mettre à jour l'icône du menu.

Pour régénérer l'icône multi-tailles à partir du PNG : `.\scripts\generate-icon.ps1`

## Compilation

### Mode développement (léger, runtime requis)

```powershell
dotnet build -c Release
```

Sortie : `bin\Release\net10.0-windows\SpaceManager.exe` (~200 Ko)

**Non autonome** : nécessite le [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) installé sur la machine. Plusieurs fichiers sont requis (`SpaceManager.exe`, `SpaceManager.dll`, etc.).

### Mode autonome (un seul .exe, runtime inclus)

```powershell
.\scripts\publish.ps1
```

Sortie : `bin\publish\win-x64\SpaceManager.exe` (~140 Mo)

**Autonome** : un seul fichier, aucune installation de .NET requise. Idéal pour distribution ou menu contextuel sur d'autres PC.

Commande équivalente :

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o bin\publish\win-x64
```

## Menu contextuel

Le menu contextuel est **installé automatiquement au lancement** de l'application (aucun script requis).

Le menu **Analyser l'espace disque avec SpaceManager** apparaît au clic droit sur :

- un dossier
- le fond d'un dossier (dossier courant)
- un disque

Pour désinstaller le menu contextuel : `.\scripts\install-context-menu.ps1 -Uninstall`

## Utilisation

- **Clic droit** sur un dossier/disque → *Analyser l'espace disque avec SpaceManager*
- Ou lancez `SpaceManager.exe "C:\chemin\du\dossier"`
- Saisissez un chemin dans la barre en haut et cliquez **Aller**, ou **Parcourir…**
- Cliquez sur **▶** pour développer un dossier et voir ses enfants
- Cliquez sur les en-têtes **Nom** ou **Taille** pour trier
- La colonne **Utilisation** montre une barre relative : plus elle est longue, plus l'élément occupe d'espace parmi ses frères

## Notes

- Le calcul peut prendre du temps sur de gros dossiers ; un indicateur `…` s'affiche pendant le calcul
- Les dossiers ou fichiers inaccessibles sont ignorés silencieusement
- L'enregistrement du menu contextuel se fait dans `HKCU\Software\Classes` (aucun droit administrateur requis)
