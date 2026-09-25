import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Runtime config (public/config.json) so one build deploys to any environment; CD overwrites the file. */
@Injectable({ providedIn: 'root' })
export class AppConfig {
  private http = inject(HttpClient);
  apiBaseUrl = 'http://localhost:5080/api';

  async load(): Promise<void> {
    try {
      const cfg = await firstValueFrom(this.http.get<{ apiBaseUrl: string }>('/config.json'));
      if (cfg?.apiBaseUrl) this.apiBaseUrl = cfg.apiBaseUrl.replace(/\/$/, '');
    } catch {
      /* keep default */
    }
  }
}
