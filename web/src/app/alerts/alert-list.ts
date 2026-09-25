import { Component, inject, OnInit, signal } from '@angular/core';
import { CurrencyPipe, DatePipe, PercentPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AlertSummary, ApiService, Stats } from '../api';

@Component({
  selector: 'app-alert-list',
  imports: [RouterLink, CurrencyPipe, DatePipe, PercentPipe],
  template: `
    <section class="stats">
      @if (stats(); as s) {
        <div><span>Agent runs</span><strong>{{ s.runs }}</strong></div>
        <div><span>OpenAI spend</span><strong>{{ s.totalCostUsd | currency: 'USD' : 'symbol' : '1.2-4' }}</strong></div>
        <div><span>Tokens in / out</span><strong>{{ s.inputTokens }} / {{ s.outputTokens }}</strong></div>
        <div>
          <span>Analyst agreement</span>
          <strong>{{ s.analystAgreementRate === null ? '—' : (s.analystAgreementRate | percent) }}</strong>
        </div>
        <div>
          <span>Accuracy vs expected ({{ s.labelledAlertsReviewed }} reviewed)</span>
          <strong>{{ s.accuracyVsExpected === null ? '—' : (s.accuracyVsExpected | percent) }}</strong>
        </div>
      }
    </section>

    <div class="toolbar">
      <h2>Alert queue</h2>
      <span>
      <button (click)="reset()" title="Deletes agent runs and reopens all alerts. No OpenAI calls.">Reset demo</button>
      <select (change)="load($any($event.target).value)">
        <option value="">All</option>
        <option value="open">Open</option>
        <option value="reviewed">Reviewed</option>
        <option value="closed">Closed</option>
      </select>
      </span>
    </div>

    @if (error()) {
      <p class="error">{{ error() }}</p>
    }
    @if (loading()) {
      <p class="muted">Loading… (first call after idle can take ~20s while the free tier wakes up)</p>
    }

    <table>
      <thead>
        <tr>
          <th>#</th><th>Flagged</th><th>Customer</th><th>Merchant</th><th class="num">Amount</th>
          <th>Where</th><th>Reason</th><th>Status</th><th>Agent</th><th>Expected</th>
        </tr>
      </thead>
      <tbody>
        @for (a of alerts(); track a.id) {
          <tr>
            <td><a [routerLink]="['/alerts', a.id]">{{ a.id }}</a></td>
            <td>{{ a.createdAt | date: 'MMM d, HH:mm' }}</td>
            <td>{{ a.customer }}</td>
            <td>{{ a.merchant }}</td>
            <td class="num">{{ a.amount | currency: a.currency }}</td>
            <td>{{ a.country }} · {{ a.channel }}</td>
            <td>{{ a.reason }}</td>
            <td>{{ a.status }}</td>
            <td>
              @if (a.latestDecision) {
                <span class="badge" [class]="a.latestDecision.toLowerCase()">{{ a.latestDecision }}</span>
              }
            </td>
            <td class="muted">{{ a.expectedDecision ?? '' }}</td>
          </tr>
        } @empty {
          @if (!loading()) {
            <tr><td colspan="10" class="muted">No alerts.</td></tr>
          }
        }
      </tbody>
    </table>
  `,
})
export class AlertListComponent implements OnInit {
  private api = inject(ApiService);
  alerts = signal<AlertSummary[]>([]);
  stats = signal<Stats | null>(null);
  loading = signal(false);
  error = signal<string | null>(null);

  private status = '';

  ngOnInit() {
    this.load('');
  }

  reset() {
    this.api.resetDemo().subscribe({ next: () => this.load(this.status) });
  }

  load(status: string) {
    this.status = status;
    this.api.stats().subscribe({ next: (s) => this.stats.set(s), error: () => {} });
    this.loading.set(true);
    this.error.set(null);
    this.api.alerts(status || undefined).subscribe({
      next: (rows) => {
        this.alerts.set(rows);
        this.loading.set(false);
      },
      error: (e) => {
        this.error.set(`Could not reach the API (${e.status || 'network'}). Is it running?`);
        this.loading.set(false);
      },
    });
  }
}
