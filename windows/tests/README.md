# Tests du remplacement des fichiers

Les tests exécutent `UpdateInstaller`, utilisé par la mise à jour de l'application,
dans un dossier temporaire. Aucun fichier de l'installation réelle n'est touché.

Sous Windows, depuis la racine du dépôt :

```powershell
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /out:"$env:TEMP\bsidedesk-update-tests.exe" windows\src\UpdateInstaller.cs windows\tests\UpdateInstallerTests.cs
& "$env:TEMP\bsidedesk-update-tests.exe"
```

Sous Linux avec Mono (également exécuté dans le job de publication Windows) :

```sh
mcs -out:/tmp/bsidedesk-update-tests.exe windows/src/UpdateInstaller.cs windows/tests/UpdateInstallerTests.cs
mono /tmp/bsidedesk-update-tests.exe
```

Cas vérifiés : succès, restauration après échec du marqueur final (donc après le
remplacement de l'exécutable), suppression des nouveaux fichiers lors de la
restauration, archive incomplète et nettoyage des fichiers préparés. Le verrouillage
de l'exécutable est testé uniquement sous Windows, dont les règles de partage de
fichiers diffèrent de Linux.

Une recette avec l'application en cours d'exécution reste nécessaire pour vérifier
la mise à jour et le redémarrage complets. La restauration ne couvre pas un arrêt
brutal du processus ni une coupure de courant.
