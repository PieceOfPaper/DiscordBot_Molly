# Lightsail 데이터 수동 백업과 서버 이전

Molly가 실행 중인 Lightsail 인스턴스에서 SQLite DB를 수동으로 백업하고, 나중에 다른 서버로 옮기는 절차다. 백업 파일은 **서버 밖(개인 PC 등)에 보관**한다. 실행 파일의 배포 백업(`/opt/molly/app-previous`)은 DB 백업이 아니다.

## 1. 백업 대상과 실제 경로 확인

- DB: `molly.sqlite` (캐릭터 등록 정보, 이벤트 마감 알림 설정 등 SQLite 데이터).
- 기본 DB 경로: `MollyDatabase__Path`가 지정되었으면 그 경로, 아니면 `MOLLY_DATA_DIR/database/molly.sqlite`.
- 기존 [배포 문서](lightsail-deployment.md)의 데이터 디렉터리는 `/var/lib/molly`지만, 현재 `scripts/deploy-lightsail.sh`에는 `/opt/molly/data`를 지정하는 systemd 설정이 있다. **어느 경로가 실제 사용 중인지 확인한 뒤 백업한다.** 앱 설정에서 `MollyDatabase:Path`를 별도로 지정했을 수도 있다.
- `MOLLY_DATA_DIR` 아래에는 DB 외에 룬·쪽지·배틀 등의 로컬 캐시나 기타 데이터가 있을 수 있다. DB 백업 하나에 이 파일들이 들어가지는 않는다. 완전한 서버 이전이 필요하면 아래의 'DB 외 데이터' 항목도 확인한다.

Lightsail 콘솔에서 인스턴스의 브라우저 SSH에 접속하여 다음 명령을 실행한다. 이 명령은 DB 경로 관련 값만 출력하며, 환경 파일 전체나 토큰을 출력하지 않는다.

```bash
sudo systemctl is-active molly
sudo sh -c 'tr "\0" "\n" < /proc/$(systemctl show -p MainPID --value molly)/environ' | grep -E '^(MOLLY_DATA_DIR|MollyDatabase__Path)='
sudo find /opt/molly/data /var/lib/molly -type f -name molly.sqlite -print 2>/dev/null
```

서비스가 실행 중인데 `MOLLY_DATA_DIR`이 보이지 않는다면 앱 기본 경로(`/opt/molly/app/data`)도 확인한다. `MollyDatabase__Path`가 보이면 그 파일을 우선한다. 둘 이상의 DB가 보일 때에는 수정 시각만으로 선택하지 말고 **실행 중인 서비스 설정과 일치하는 경로**를 확인한다. DB 위치를 코드에서 계산하는 규칙은 `Commons/MollyDataPaths.cs`에 있다.

## 2. SQLite DB 백업하기 (기존 서버)

아래 `MOLLY_DB_PATH`의 값은 **1단계에서 확인한 실제 파일 경로**로 교체한다. `sqlite3 --version` 명령이 없다면 서버에서 최초 1회 `sudo apt-get update`와 `sudo apt-get install -y sqlite3`를 실행한다.

```bash
MOLLY_DB_PATH=/var/lib/molly/database/molly.sqlite
sudo test -f "$MOLLY_DB_PATH" && echo "원본 DB 확인: $MOLLY_DB_PATH"
```

원본 DB 확인 메시지가 나오지 않으면 **다음 명령을 실행하지 말고** 경로를 다시 확인한다. DB가 없는 경로를 SQLite로 열면 빈 DB를 새로 만들 수 있다.

```bash
MOLLY_BACKUP="/home/ubuntu/molly-$(date +%Y%m%d-%H%M%S).sqlite"
sudo sqlite3 "$MOLLY_DB_PATH" ".backup '$MOLLY_BACKUP'"
sudo chown ubuntu:ubuntu "$MOLLY_BACKUP"
chmod 600 "$MOLLY_BACKUP"
sqlite3 "$MOLLY_BACKUP" 'PRAGMA integrity_check;'
ls -lh "$MOLLY_BACKUP"
```

`integrity_check` 결과가 `ok`이고 백업 파일 크기가 0이 아닌지 확인한다. SQLite의 `.backup`은 실행 중인 DB를 일관된 파일로 복사하므로 평소 백업에는 봇을 멈출 필요가 없다. 실행 중인 `molly.sqlite`를 `cp`로 단독 복사하면 WAL에 기록된 변경 사항이 빠질 수 있으므로 `.backup`을 사용한다.

## 3. 백업 파일을 PC에 보관하기

Mac 터미널에서 실행한다. 2단계에서 출력된 **정확한 파일명**과 자신의 서버 IP·SSH 키 경로로 교체한다. 예시의 IP와 키 경로는 저장소에 기록하지 않는다.

```bash
scp -i /path/to/lightsail-key.pem \
  ubuntu@LIGHTSAIL_IP:/home/ubuntu/molly-YYYYMMDD-HHMMSS.sqlite \
  ~/Downloads/
```

SSH 별칭 `molly-lightsail`을 이미 설정했다면 `scp molly-lightsail:/home/ubuntu/파일명.sqlite ~/Downloads/`처럼 사용할 수 있다. 터미널 대신 FileZilla(SFTP)로 `ubuntu` 계정과 SSH 키를 설정해 `/home/ubuntu`에서 파일을 내려받아도 된다. 로컬 파일 크기를 확인하고, 가능하면 로컬에서도 `sqlite3 ~/Downloads/파일명.sqlite 'PRAGMA integrity_check;'`가 `ok`인지 확인한다. **다운로드를 확인한 뒤에만** 서버의 임시 백업 파일을 삭제한다. SSH 개인키나 `/etc/molly/molly.env`를 공개 저장소에 올리지 않는다.

## 4. 다른 서버로 DB 옮기기

1. 실제 이전을 시작할 때 기존 서버의 봇을 중지하고, 마지막 DB 백업을 새로 만들어 PC로 내려받는다. 새 서버에 Molly를 설치한 뒤 새 서버의 봇도 `sudo systemctl stop molly`로 멈춘다. 설치 과정에서 봇이 잠깐 시작될 수 있으므로, 같은 토큰을 쓰는 기존 서버를 먼저 중지한다. 새 서버의 `MOLLY_DATA_DIR` 및 `MollyDatabase__Path` 설정에 따라 **실제 사용할 DB 경로**를 정한다.
2. PC에 보관한 백업 파일을 `scp` 또는 SFTP로 새 서버의 `/home/ubuntu`에 전송한다.
3. 새 서버에서 아래 예시의 경로와 백업 파일명을 실제 값으로 교체해 DB를 설치한다. **기존 DB가 있다면 먼저 별도로 보관**한다.

   ```bash
   MOLLY_DB_PATH=/var/lib/molly/database/molly.sqlite
   MOLLY_BACKUP=/home/ubuntu/molly-YYYYMMDD-HHMMSS.sqlite
   sudo test -s "$MOLLY_BACKUP" && sqlite3 "$MOLLY_BACKUP" 'PRAGMA integrity_check;'
   sudo install -d -o molly -g molly -m 750 "$(dirname "$MOLLY_DB_PATH")"
   sudo install -o molly -g molly -m 600 "$MOLLY_BACKUP" "$MOLLY_DB_PATH"
   sudo systemctl start molly
   sudo systemctl status molly --no-pager
   ```

   무결성 검사에서 `ok`를 확인한 경우에만 `install`을 진행한다. 새 서버의 systemd 서비스 계정이 `molly`가 아니라면 `-o`, `-g` 값도 그 계정에 맞춘다. 새 서버의 **상위 데이터 디렉터리에도 해당 계정의 접근 권한**이 있어야 한다.
4. 봇에서 등록 캐릭터나 알림 설정이 기존 데이터대로 보이는지 확인한다. 신규 서버에서 DB를 처음 생성하도록 봇을 먼저 구동했다면, 서비스를 멈춘 상태에서 그 DB를 백업 파일로 교체한다.

### DB 외 데이터와 설정

`MOLLY_DATA_DIR`의 나머지 파일은 DB 백업에 포함되지 않는다. 재생성 가능한 캐시만 있다면 새 서버에서 다시 받아올 수 있지만, 보존할 파일이 있는지 `sudo find /실제/MOLLY_DATA_DIR -type f`로 확인한다. 필요하면 기존 서버에서 서비스를 멈춘 동안 해당 디렉터리를 별도 아카이브로 복사해 새 서버로 옮긴다. 원본과 대상의 `MOLLY_DATA_DIR`이 서로 달라도 상대 경로를 유지해서 옮긴다.

`/etc/molly/molly.env` 등 Discord 토큰과 인증 설정도 DB에 들어 있지 않다. 새 서버에 별도로 안전하게 설정하고 공개 저장소에는 기록하지 않는다. 이전 작업 중 두 서버에서 같은 봇 토큰으로 동시에 실행하지 않도록 기존 서버의 서비스를 중지한 뒤 새 서버를 시작한다.

## 참고

- [SQLite 명령줄 `.backup`](https://sqlite.org/cli.html), [온라인 백업 API](https://sqlite.org/backup.html), [무결성 검사](https://sqlite.org/pragma.html#pragma_integrity_check)
- [AWS Lightsail에서 SCP로 파일 전송](https://docs.aws.amazon.com/lightsail/latest/userguide/amazon-lightsail-transfer-files-between-linux-instances.html), [SFTP로 파일 전송](https://docs.aws.amazon.com/lightsail/latest/userguide/amazon-lightsail-connecting-to-linux-unix-instance-using-sftp.html)
