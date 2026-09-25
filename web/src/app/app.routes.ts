import { Routes } from '@angular/router';
import { AlertListComponent } from './alerts/alert-list';
import { AlertDetailComponent } from './alerts/alert-detail';

export const routes: Routes = [
  { path: '', component: AlertListComponent, title: 'Alert queue' },
  { path: 'alerts/:id', component: AlertDetailComponent, title: 'Alert review' },
  { path: '**', redirectTo: '' },
];
