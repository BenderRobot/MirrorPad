# Tests d'authentification

Depuis la racine du dépôt, avec un compilateur C :

```sh
cc -std=c99 -Wall -Wextra -Werror -pedantic ipad/tests/handshake.c -o /tmp/bsidedesk-handshake
/tmp/bsidedesk-handshake
```

Ces tests exécutent la logique de transition utilisée dans `bSideDesk.m` via
`MPHandshake.h` : succès prématuré, réponse d'un autre parcours, réponse tronquée,
jeton invalide, HELLO répété et réponse rejouée. Les deux parcours valides sont
également testés. Ils bloquent le job iPad de publication en cas d'échec.

Ils ne remplacent pas la compilation Objective-C ni une recette sur l'appareil.
Sur l'iPad, vérifier une reconnexion à un PC connu, un nouvel appairage, le refus
d'un certificat changé et le refus d'un OK envoyé avant HELLO (sans notification
d'authentification ni traitement de commandes). Vérifier aussi HELLO d'un nouveau
PC suivi de OK : la liaison doit se fermer sans exception ni PC mémorisé.
